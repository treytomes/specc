using System.Reflection;

namespace Specc.Graph;

/// <summary>IL value type used in an intrinsic descriptor signature.</summary>
public enum IrType
{
    /// <summary>No return value.</summary>
    Void,
    /// <summary>32-bit integer.</summary>
    Int,
    /// <summary>Managed string.</summary>
    String,
}

/// <summary>A single IL emission step within an intrinsic sequence.</summary>
public abstract record IntrinsicStep;
/// <summary>Emits a static method call.</summary>
/// <param name="Method">The static method to call.</param>
public record StaticCall(MethodInfo Method)   : IntrinsicStep;
/// <summary>Emits a virtual (callvirt) method call.</summary>
/// <param name="Method">The virtual method to call.</param>
public record VirtualCall(MethodInfo Method)  : IntrinsicStep;
/// <summary>Emits a static property getter call.</summary>
/// <param name="Property">The static property whose getter to call.</param>
public record StaticGet(PropertyInfo Property) : IntrinsicStep;

/// <summary>Describes a named intrinsic operation and its IL emission sequence.</summary>
/// <param name="Name">Logical name used in stack IR, e.g. <c>console.write_line.int</c>.</param>
/// <param name="Steps">Ordered IL steps emitted by <see cref="Specc.Passes.AssemblyEmitPass"/>.</param>
/// <param name="Inputs">Types of stack values consumed by this intrinsic.</param>
/// <param name="Returns">Type pushed onto the stack after the intrinsic executes.</param>
/// <param name="IlText">Textual IL emitted by <see cref="Specc.Passes.MsilGenerationPass"/>.</param>
/// <param name="EmbeddingHint">Human-readable description used as the embedding input.</param>
public record IntrinsicDescriptor(
    string                      Name,
    IReadOnlyList<IntrinsicStep> Steps,
    IrType[]                    Inputs,
    IrType                      Returns,
    string                      IlText,
    string                      EmbeddingHint
);

/// <summary>Registry of built-in intrinsic operations available to the code generator.</summary>
public static class IntrinsicLibrary
{
    private static readonly Dictionary<string, IntrinsicDescriptor> Table = [];

    static IntrinsicLibrary() => RegisterAll();

    /// <summary>Returns the descriptor for the named intrinsic, or throws if unknown.</summary>
    public static IntrinsicDescriptor Get(string name) =>
        Table.TryGetValue(name, out var d) ? d
            : throw new InvalidOperationException($"Unknown intrinsic: '{name}'");

    /// <summary>Attempts to look up the named intrinsic; returns false when not found.</summary>
    public static bool TryGet(string name, out IntrinsicDescriptor descriptor) =>
        Table.TryGetValue(name, out descriptor!);

    private static void Register(IntrinsicDescriptor d) => Table[d.Name] = d;

    private static void RegisterAll()
    {
        Register(new(
            Name:  "console.read_line",
            Steps: [new StaticCall(typeof(Console).GetMethod("ReadLine", Type.EmptyTypes)!)],
            Inputs: [],
            Returns: IrType.String,
            IlText: "call string [mscorlib]System.Console::ReadLine()",
            EmbeddingHint: "Reads a line of text from standard input."
        ));

        Register(new(
            Name:  "int.parse",
            Steps: [new StaticCall(typeof(int).GetMethod("Parse", [typeof(string)])!)],
            Inputs: [IrType.String],
            Returns: IrType.Int,
            IlText: "call int32 [mscorlib]System.Int32::Parse(string)",
            EmbeddingHint: "Parses a string into a 32-bit integer."
        ));

        Register(new(
            Name:  "string.concat",
            Steps: [new StaticCall(typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!)],
            Inputs: [IrType.String, IrType.String],
            Returns: IrType.String,
            IlText: "call string [mscorlib]System.String::Concat(string, string)",
            EmbeddingHint: "Concatenates two strings."
        ));

        Register(new(
            Name:  "console.write_line.string",
            Steps: [new StaticCall(typeof(Console).GetMethod("WriteLine", [typeof(string)])!)],
            Inputs: [IrType.String],
            Returns: IrType.Void,
            IlText: "call void [mscorlib]System.Console::WriteLine(string)",
            EmbeddingHint: "Writes a string followed by a newline to standard output."
        ));

        Register(new(
            Name:  "console.write_line.int",
            Steps: [new StaticCall(typeof(Console).GetMethod("WriteLine", [typeof(int)])!)],
            Inputs: [IrType.Int],
            Returns: IrType.Void,
            IlText: "call void [mscorlib]System.Console::WriteLine(int32)",
            EmbeddingHint: "Writes an integer followed by a newline to standard output."
        ));

        // rand_int: push Random.Shared, then call Next(min, max).
        // min and max must already be on the stack before this intrinsic is emitted.
        Register(new(
            Name:  "rand_int",
            Steps: [
                new StaticGet(typeof(Random).GetProperty("Shared")!),
                new VirtualCall(typeof(Random).GetMethod("Next", [typeof(int), typeof(int)])!),
            ],
            Inputs: [IrType.Int, IrType.Int],
            Returns: IrType.Int,
            IlText: "call class [mscorlib]System.Random [mscorlib]System.Random::get_Shared()\n    callvirt instance int32 [mscorlib]System.Random::Next(int32, int32)",
            EmbeddingHint: "Returns a random integer between min (inclusive) and max (exclusive)."
        ));
    }
}
