[expect php]
[file]
<?php
// As a map from objects to data
$s = new SplObjectStorage();

$o1 = new StdClass;
$o2 = new StdClass;
$o3 = new StdClass;

$s[$o1] = "data for object 1";
$s[$o2] = "two";
$s[$o3] = 3;

var_dump($s->key(), $s->valid());

?>