namespace Specc.Graph;

public enum OpCode
{
    LdcI4,   // push int constant
    LdlocS,  // load scalar local; operand = variable name
    StlocS,  // store scalar local; operand = variable name
    Add,
    Rem,     // remainder (modulo)
    Ceq,     // compare equal
    Brfalse, // branch if false (pops bool)
    Br,      // unconditional branch
    LdstrS,   // load string constant
    Intrinsic, // call named intrinsic; operand = IntrinsicLibrary name
    Ret,
    Label,   // pseudo-op: define label
    Cgt,     // compare greater-than
    Brtrue,  // branch if true (pops bool)
    Sub,       // subtract
    Mul,       // multiply
    Newarr,    // allocate int array; no operand (size already on stack)
    LdlocA,    // load array local; operand = array name
    StlocA,    // store array local; operand = array name
    LdelemI4,  // load int element from array (pops arr ref and index, pushes int)
    StelemI4,  // store int element into array (pops arr ref, index, value)
    LdlocStr,  // load string local; operand = variable name
    StlocStr,  // store string local; operand = variable name
    Clt,       // compare less-than; pops two ints, pushes 1 if second < top
    Div,       // integer divide; pops two ints, pushes quotient
    RandInt,   // Random.Shared.Next(min, max+1) → store; operand = "name:min:max"
}

public record StackInstruction(OpCode Op, string? Operand = null);
