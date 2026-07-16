namespace IronLlm.Passes;

public class CompilationException : Exception
{
    public CompilationException(string message) : base(message) { }
    public CompilationException(string message, Exception inner) : base(message, inner) { }
}
