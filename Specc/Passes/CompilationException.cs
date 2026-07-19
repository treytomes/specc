namespace Specc.Passes;

/// <summary>Thrown when a compiler pass encounters an unrecoverable error.</summary>
public class CompilationException : Exception
{
    /// <summary>Initialises the exception with an error message.</summary>
    public CompilationException(string message) : base(message) { }

    /// <summary>Initialises the exception with an error message and an inner exception.</summary>
    public CompilationException(string message, Exception inner) : base(message, inner) { }
}
