namespace IronLlm.Graph;

public enum OpCode
{
    LdcI4,   // push int constant
    LdlocS,  // load local
    StlocS,  // store local
    Add,
    Rem,     // remainder (modulo)
    Ceq,     // compare equal
    Brfalse, // branch if false (pops bool)
    Br,      // unconditional branch
    LdstrS,  // load string constant
    Call,    // call method
    Ret,
    Label,   // pseudo-op: define label
    Cgt,     // compare greater-than
    Brtrue,  // branch if true (pops bool)
}

public record StackInstruction(OpCode Op, string? Operand = null);
