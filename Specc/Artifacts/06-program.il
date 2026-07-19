.assembly FizzBuzz {}
.class public FizzBuzz {
  .method public static void Main() cil managed {
    .entrypoint
    .locals init (int32 V_0)

  entry:
    br loop_test
  loop_test:
    brfalse exit
  fizzbuzz_check:
    ldloc.0
    ldc.i4 15
    rem
    ldc.i4 0
    ceq
    brfalse fizz_check
  fizz_check:
    ldloc.0
    ldc.i4 3
    rem
    ldc.i4 0
    ceq
    brfalse buzz_check
  buzz_check:
    ldloc.0
    ldc.i4 5
    rem
    ldc.i4 0
    ceq
    brfalse loop_inc
  print_n:
    ldloc.0
    call void [mscorlib]System.Console::WriteLine(int32)
    br loop_inc
  loop_inc:
    br loop_test
  exit:
    ret
  }
}
