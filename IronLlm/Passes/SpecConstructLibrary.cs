using System.Text;

namespace IronLlm.Passes;

/// <summary>
/// Owns the per-family sections of the extraction system prompt.
/// Call Assemble(tags) to build a focused prompt containing only the constructs
/// needed for a given program, keeping each extraction call within a small token window.
/// </summary>
public static class SpecConstructLibrary
{
    public static string Preamble => """
        You are a compiler front-end. Read a program description in Markdown and extract it as a structured .spec file.

        The .spec format starts with:

          program: <name>

        """;

    public static string LoopSection => """

        For programs that iterate over a fixed range of integers, use a loop: block:

          loop:
            from: <int>
            to: <int>

        The loop counter is incremented automatically — do NOT add an assign: that increments it.

        """;

    public static string BranchSection => """

        For conditional output inside a loop, use branch: blocks (evaluated in declaration order):

          branch:
            condition: <snake_case_name>
            divisor: <int>                # modulo check: fires when counter % divisor == 0; omit if not a divisor check
            compare: lt | gt | eq         # omit unless branching on a runtime variable's value
            value: <int>                  # integer rhs for compare:; required with compare:
            true_output: "<string>"       # output when condition is true; use {variable} for the counter value

        Include a branch with no divisor as the default/fallback:
          branch:
            condition: default
            true_output: "{n}"

        Use snake_case for all condition names.
        For runtime value comparisons (e.g. "if n < 42") use compare: + value:, NOT divisor:.

        """;

    public static string ArithSection => """

        For arithmetic on variables, use assign: blocks:

          assign:
            target: <identifier>
            op: mul | add | sub | div | copy
            left: {variable} | <int>
            right: {variable} | <int>     # omit when op is copy

        The "copy" op copies one variable into another (no right operand):
          assign:
            target: tmp
            op: copy
            left: {a}

        For {variable} operands use braces: {a}. For integer constants use the number directly: 7.
        Use assign: for ALL arithmetic. Do NOT use branch:/divisor: for arithmetic.

        """;

    public static string InputSection => """

        For programs that read user input, use variable: with source: stdin:

          variable:
            name: <identifier>
            type: int | string
            source: stdin

        For unconditional output (no condition), use print::
          print: "<string or {variable}>"

        For programs that only read input and print — no loop:, no branch::
          program: Greetings
          print: "Hello! What is your name?"
          variable:
            name: user_name
            type: string
            source: stdin
          print: "{user_name}"

        """;

    public static string ArraySection => """

        For programs that operate on a fixed-size array, declare it with variable: and an array type:

          variable:
            name: <identifier>
            type: array[int]
            initial_value: [<int>, <int>, ...]

        """;

    public static string WhileSection => """

        For programs with an unbounded loop (e.g. "repeat until n equals 1"), use while::

          while:
            variable: <identifier>
            condition: ne | eq | lt | gt
            value: <int>

        For a while loop that compares two variables (e.g. "repeat until guess equals target"), use:

          while:
            compare_lhs: {variable}
            compare: ne | eq | lt | gt
            compare_rhs: {variable}

        Inside a while: body, conditional assignments use true_assign: instead of true_output::

          branch:
            condition: <snake_case_name>
            divisor: <int>              # optional; fires when variable % divisor == 0
            true_assign:
              target: <identifier>
              op: mul | add | sub | div | copy
              left: {variable} | <int>
              right: {variable} | <int>

        A branch: with no divisor inside a while: is the default/else path (assign always executed).
        Multiple true_assign: blocks under one branch: are executed in order.

        For a while loop with runtime comparisons (lt/gt/eq between a variable and another variable),
        use compare: and compare_with: on branch: blocks instead of divisor::

          branch:
            condition: <snake_case_name>
            compare: lt | gt | eq
            compare_with: {variable}    # variable rhs instead of value: <int>
            true_output: "<string>"

        """;

    public static string RandomSection => """

        For programs that need a random integer, use random::

          random:
            name: <identifier>
            min: <int>
            max: <int>

        This declares a variable whose value is a random integer in [min, max] (inclusive).
        Use the variable name with braces ({name}) to reference it in branch: or print: blocks.

        """;

    public static string Rules => """

        Rules:
        1. Output ONLY the .spec content — no explanation, no markdown fences.
        2. If the document describes a program that cannot be expressed in this format, output a single line: ERROR: <reason>
        """;

    public static string Assemble(IEnumerable<string> tags)
    {
        var sb = new StringBuilder(Preamble);
        foreach (var tag in tags)
        {
            sb.Append(tag switch
            {
                "loop"       => LoopSection,
                "branch"     => BranchSection,
                "arithmetic" => ArithSection,
                "input"      => InputSection,
                "array"      => ArraySection,
                "while"      => WhileSection,
                "random"     => RandomSection,
                _            => "",
            });
        }
        sb.Append(Rules);
        return sb.ToString();
    }
}
