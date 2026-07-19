namespace Specc.Graph;

/// <summary>Stack-based IL opcode used in the Specc stack IR.</summary>
public enum OpCode
{
    /// <summary>Push an integer constant onto the stack.</summary>
    LdcI4,
    /// <summary>Load a scalar integer local variable; operand is the variable name.</summary>
    LdlocS,
    /// <summary>Store the top-of-stack integer into a scalar local; operand is the variable name.</summary>
    StlocS,
    /// <summary>Add the top two stack values.</summary>
    Add,
    /// <summary>Compute the remainder (modulo) of the top two stack values.</summary>
    Rem,
    /// <summary>Compare the top two stack values for equality; push 1 if equal.</summary>
    Ceq,
    /// <summary>Branch to the operand label if the top-of-stack value is false (zero).</summary>
    Brfalse,
    /// <summary>Unconditionally branch to the operand label.</summary>
    Br,
    /// <summary>Load a string literal onto the stack; operand is the string value.</summary>
    LdstrS,
    /// <summary>Call a named intrinsic; operand is the <see cref="IntrinsicLibrary"/> key.</summary>
    Intrinsic,
    /// <summary>Return from the current method.</summary>
    Ret,
    /// <summary>Pseudo-op defining a branch target label; operand is the label name.</summary>
    Label,
    /// <summary>Compare greater-than; push 1 if the second-top value is greater than the top.</summary>
    Cgt,
    /// <summary>Branch to the operand label if the top-of-stack value is true (non-zero).</summary>
    Brtrue,
    /// <summary>Subtract the top stack value from the value below it.</summary>
    Sub,
    /// <summary>Multiply the top two stack values.</summary>
    Mul,
    /// <summary>Allocate a new integer array; size is already on the stack.</summary>
    Newarr,
    /// <summary>Load an integer array local; operand is the array variable name.</summary>
    LdlocA,
    /// <summary>Store the top-of-stack array reference into an array local; operand is the array variable name.</summary>
    StlocA,
    /// <summary>Load a 32-bit integer element from an array (pops array ref and index, pushes int).</summary>
    LdelemI4,
    /// <summary>Store a 32-bit integer element into an array (pops array ref, index, and value).</summary>
    StelemI4,
    /// <summary>Load a string local variable; operand is the variable name.</summary>
    LdlocStr,
    /// <summary>Store the top-of-stack string into a string local; operand is the variable name.</summary>
    StlocStr,
    /// <summary>Compare less-than; push 1 if the second-top value is less than the top.</summary>
    Clt,
    /// <summary>Integer divide the top two stack values; push the quotient.</summary>
    Div,
    /// <summary>Generate a random integer via <c>Random.Shared.Next(min, max)</c>; operand is <c>"name:min:max"</c>.</summary>
    RandInt,
}

/// <summary>A single instruction in the Specc stack IR.</summary>
/// <param name="Op">The opcode.</param>
/// <param name="Operand">Optional string operand whose meaning depends on the opcode.</param>
public record StackInstruction(OpCode Op, string? Operand = null);
