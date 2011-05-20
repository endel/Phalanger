﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Reflection.Emit;

using PHP.Core;
using PHP.Core.Emit;
using PHP.Core.AST;

namespace PHP.Core.Compiler.CodeGenerator
{
    /// <summary>
    /// CallSite fields manager and emitter.
    /// </summary>
    internal class CallSitesBuilder
    {
        #region Fields & Properties

        /// <summary>
        /// The module that will contain the call sites container.
        /// </summary>
        private readonly ModuleBuilder/*!*/moduleBuilder;

        /// <summary>
        /// The name used to identify the call sites container. It consists of location identifier.
        /// </summary>
        private readonly string/*!*/containerClassName;

        /// <summary>
        /// The unique id to name the container class.
        /// </summary>
        private static int nextContainerId = 0;

        /// <summary>
        /// Lazily initialized class contained static instances of declared call sites.
        /// </summary>
        private TypeBuilder containerClass;

        /// <summary>
        /// .cctor of the call sites container. Not null if <see cref="containerClass"/> has been initialized.
        /// </summary>
        private ILEmitter staticCtorEmitter;

        /// <summary>
        /// If known and if it can be emitted in static .cctor, defines the place where the class context can be loaded.
        /// Otherwise <c>null</c>, the class context will be determined in run time and passed to binder.
        /// </summary>
        private readonly IPlace classContextPlace;

        #endregion

        #region Constructors

        /// <summary>
        /// Create new call sites builder.
        /// </summary>
        /// <param name="moduleBuilder">Module to contain call sites container.</param>
        /// <param name="userFriendlyName">User friendly name used to identify the call sites container by user.</param>
        /// <param name="classContextPlace">If known and if it can be emitted in static .cctor, defines the place where the class context can be loaded. Otherwise <c>null</c> if the class context will be determined in run time.</param>
        public CallSitesBuilder(ModuleBuilder/*!*/moduleBuilder, string/*!*/userFriendlyName, IPlace classContextPlace)
        {
            Debug.Assert(moduleBuilder != null && userFriendlyName != null);

            this.moduleBuilder = moduleBuilder;
            this.containerClassName = string.Format("<{0}>o_Sitescontainer#{1}", userFriendlyName.Replace('.','_'), System.Threading.Interlocked.Increment(ref nextContainerId));
            this.classContextPlace = classContextPlace;
        }

        #endregion

        #region EnsureContainer, Bake, DefineCallSite

        /// <summary>
        /// Ensure the call sites container is created and return the <see cref="TypeBuilder"/>.
        /// </summary>
        /// <returns></returns>
        private TypeBuilder/*!*/EnsureContainer()
        {
            if (containerClass == null)
            {
                Debug.Assert(staticCtorEmitter == null);

                containerClass = moduleBuilder.DefineType(containerClassName, TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.NotPublic | TypeAttributes.Abstract);
                staticCtorEmitter = new ILEmitter(containerClass.DefineTypeInitializer());
            }

            return containerClass;
        }

        /// <summary>
        /// Bake the container class if any. Close the call sites builder.
        /// </summary>
        public void Bake()
        {
            if (containerClass != null)
            {
                Debug.Assert(staticCtorEmitter != null);

                // finish static ctor:
                staticCtorEmitter.Emit(OpCodes.Ret);

                // bake the call sites container:
                containerClass.CreateType();

                // avoid of using the builders anymore:
                containerClass = null;
                staticCtorEmitter = null;
            }
        }

        /// <summary>
        /// Define new instance of CallSite&lt;<paramref name="delegateType"/>&gt; and initialize it with specified binder.
        /// </summary>
        /// <param name="userFriendlyName">User friendly name used as name for the CallSite field.</param>
        /// <param name="delegateType">CallSite type argument.</param>
        /// <param name="binderInstanceEmitter">Function used to emit initialization of the binder from within the call sites container .cctor.</param>
        /// <returns>The <see cref="FieldInfo"/> containing the instance of the created CallSite.</returns>
        public FieldInfo/*!*/DefineCallSite(string/*!*/userFriendlyName, Type/*!*/delegateType, Action<ILEmitter>/*!*/binderInstanceEmitter)
        {
            Debug.Assert(userFriendlyName != null && delegateType != null && binderInstanceEmitter != null);

            // call sites container 
            var type = EnsureContainer();

            // call site type
            var callSiteType = typeof(CallSite<>).MakeGenericType(delegateType);

            // define the field:
            // public static readonly CallSite<delegateType> <userFriendlyName>
            var field = type.DefineField(userFriendlyName, callSiteType, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);

            // init the field in .cctor:
            // <field> = CallSite<...>.Create( <BINDER> )
            binderInstanceEmitter(staticCtorEmitter);
            staticCtorEmitter.Emit(OpCodes.Call, callSiteType.GetMethod("Create", Types.CallSiteBinder));
            staticCtorEmitter.Emit(OpCodes.Stsfld, field);

            //
            return field;
        }

        #endregion

        #region EmitMethodCall

        public PhpTypeCode EmitMethodCall(
            PHP.Core.CodeGenerator/*!*/cg, /* TODO: (J) resultWanted ? resultReferenceWanted ? */
            Expression/*!*/targetExpr,
            string methodFullName, Expression methodNameExpr, CallSignature callSignature)
        {
            Debug.Assert(methodFullName != null ^ methodNameExpr != null);

            //
            bool methodNameIsKnown = (methodFullName != null);
            bool classContextIsKnown = (this.classContextPlace != null);

            //
            var delegateType = System.Linq.Expressions.Expression.GetDelegateType(MethodCallDelegateTypeArgs(methodNameIsKnown, callSignature, Types.PhpReference[0]));

            //
            var field = DefineCallSite(string.Format("invoke<{0}>", methodFullName ?? "$"), delegateType, (il) =>
            {
                il.Emit(OpCodes.Ldnull);    // TODO: (J) <LOAD> Binder.MethodCall( methodFullName, classContext.EmitLoad(), resultReferenceWanted )
            });

            // call the CallSite:

            // <field>.Target( <field>, <targetExpr>, <scriptContext>, <callSignature.EmitLoadOnEvalStack>, (classContext)?, <methodNameExpr>? ):
            cg.IL.Emit(OpCodes.Ldsfld, field);
            cg.IL.Emit(OpCodes.Ldfld, field.FieldType.GetField("Target"));
            cg.IL.Emit(OpCodes.Ldsfld, field);
            EmitTargetExpr(cg, targetExpr);
            cg.EmitLoadScriptContext();
            foreach (var t in callSignature.GenericParams) t.EmitLoadTypeDesc(cg, ResolveTypeFlags.UseAutoload | ResolveTypeFlags.ThrowErrors); // load DTypeDescs on the stack
            foreach (var p in callSignature.Parameters) { var param_type = p.Emit(cg); if (p.Expression.ValueTypeCode == PhpTypeCode.Unknown) cg.EmitBoxing(param_type); }  // load args on the stack, unknown types were passed as objects
            if (!classContextIsKnown) cg.EmitLoadClassContext();
            if (!methodNameIsKnown) cg.EmitName(methodFullName/*null*/, methodNameExpr, true);
            cg.IL.Emit(OpCodes.Callvirt, delegateType.GetMethod("Invoke"));

            //
            return PhpTypeCode.PhpReference;
        }

        #endregion

        #region Helper methods

        private static void EmitTargetExpr(PHP.Core.CodeGenerator/*!*/cg, Expression/*!*/targetExpr)
        {
            // start a new operators chain (as the rest of chain is read)
            cg.ChainBuilder.Create();
            cg.ChainBuilder.Begin();
            cg.ChainBuilder.Lengthen(); // for hop over ->

            // prepare for operator invocation
            cg.EmitBoxing(targetExpr.Emit(cg));
            cg.ChainBuilder.End();
        }

        /// <summary>
        /// Make an array containing types for CallSite generic type.
        /// </summary>
        /// <param name="nameIsKnown">Iff name is known at compile time.</param>
        /// <param name="callSignature">The method call signature.</param>
        /// <param name="returnType">The return value type.</param>
        /// <returns></returns>
        private Type[]/*!*/MethodCallDelegateTypeArgs(bool nameIsKnown, CallSignature callSignature, Type/*!*/returnType)
        {
            List<Type> typeArgs = new List<Type>(callSignature.Parameters.Count + callSignature.GenericParams.Count + 6);

            // Type[]{CallSite, object, ScriptContext, {argsType}, (DTypeDesc)?, (object)?, <returnType>}:

            // CallSite:
            typeArgs.Add(Types.CallSite[0]);

            // object instance:
            typeArgs.Add(Types.Object[0]);

            // ScriptContext:
            typeArgs.Add(Types.ScriptContext[0]);

            // parameters:
            foreach (var t in callSignature.GenericParams) typeArgs.Add(Types.DTypeDesc[0]);
            foreach (var p in callSignature.Parameters) typeArgs.Add(PhpTypeCodeEnum.ToType(p.Expression.ValueTypeCode) ?? Types.Object[0]);

            // class context (if not known at compile time):
            if (this.classContextPlace == null) typeArgs.Add(Types.DTypeDesc[0]);

            // method name (if now known at compile time):
            if (!nameIsKnown) typeArgs.Add(Types.Object[0]);
            
            // return type:
            typeArgs.Add(returnType);

            //
            return typeArgs.ToArray();
        }

        #endregion       
    }
}