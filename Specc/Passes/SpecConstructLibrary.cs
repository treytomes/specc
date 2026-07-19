using System.Text;

namespace Specc.Passes;

/// <summary>
/// Owns the per-family sections of the extraction system prompt.
/// Call Assemble(tags) to build a focused prompt containing only the constructs
/// needed for a given program, keeping each extraction call within a small token window.
/// </summary>
public static class SpecConstructLibrary
{
    /// <summary>System prompt preamble establishing the compiler front-end role.</summary>
    public static string Preamble => """
        You are a compiler front-end. Read a program description in Markdown and extract it as a structured .spec file.

        The .spec format starts with:

          program: <name>

        """;

    /// <summary>Prompt section describing <c>loop:</c> blocks for fixed-range iteration.</summary>
    public static string LoopSection => """

        For programs that iterate over a fixed range of integers, use a loop: block:

          loop:
            from: <int>
            to: <int>

        The loop counter is incremented automatically — do NOT add an assign: that increments it.

        Example (print 1 through 5):
          program: CountDown
          loop:
            from: 1
            to: 5
          variable:
            name: n
            type: int
          print: "{n}"

        """;

    /// <summary>Prompt section describing <c>branch:</c> blocks for conditional output inside a loop.</summary>
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

        Example (print Fizz if divisible by 3, else n, for 1–10):
          program: Fizz
          loop:
            from: 1
            to: 10
          variable:
            name: n
            type: int
          branch:
            condition: fizz
            divisor: 3
            true_output: "Fizz"
          branch:
            condition: default
            true_output: "{n}"

        """;

    /// <summary>Prompt section describing <c>assign:</c> blocks for arithmetic and variable copies.</summary>
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

        Example (print n×2 for n from 1 to 5):
          program: Doubles
          loop:
            from: 1
            to: 5
          variable:
            name: n
            type: int
          variable:
            name: result
            type: int
          assign:
            target: result
            op: mul
            left: {n}
            right: 2
          print: "{result}"

        """;

    /// <summary>Prompt section describing <c>variable: source: stdin</c> and unconditional <c>print:</c>.</summary>
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

        Example (read a number and print it back):
          program: Echo
          variable:
            name: n
            type: int
            source: stdin
          print: "{n}"

        """;

    /// <summary>Prompt section describing fixed-size array variable declarations.</summary>
    public static string ArraySection => """

        For programs that operate on a fixed-size array, declare it with variable: and an array type:

          variable:
            name: <identifier>
            type: array[int]
            initial_value: [<int>, <int>, ...]

        """;

    /// <summary>Prompt section describing <c>while:</c> loops including var-vs-var form and <c>true_assign:</c> bodies.</summary>
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

        Example (keep halving n until it equals 1):
          program: Halve
          variable:
            name: n
            type: int
            source: stdin
          while:
            variable: n
            condition: ne
            value: 1
          print: "{n}"
          branch:
            condition: default
            true_assign:
              target: n
              op: div
              left: {n}
              right: 2

        Example (interactive: keep reading guesses until guess equals answer):
          program: SimpleGuess
          variable:
            name: answer
            type: int
            initial_value: 42
          while:
            compare_lhs: {guess}
            compare: ne
            compare_rhs: {answer}
          variable:
            name: guess
            type: int
            source: stdin
          branch:
            condition: too_low
            compare: lt
            compare_with: {answer}
            true_output: "Too low!"
          branch:
            condition: too_high
            compare: gt
            compare_with: {answer}
            true_output: "Too high!"

        """;

    /// <summary>Prompt section describing <c>random:</c> blocks for generating random integers.</summary>
    public static string RandomSection => """

        For programs that need a random integer, use random::

          random:
            name: <identifier>
            min: <int>
            max: <int>

        This declares a variable whose value is a random integer in [min, max] (inclusive).
        Use the variable name with braces ({name}) to reference it in branch: or print: blocks.

        Example (pick a number 1–6 and print it):
          program: DiceRoll
          random:
            name: roll
            min: 1
            max: 6
          print: "{roll}"

        """;

    /// <summary>Closing rules appended to every assembled prompt reminding the model to output only spec content.</summary>
    public static string Rules => """

        Rules:
        1. Output ONLY the .spec content — no explanation, no markdown fences.
        2. If the document describes a program that cannot be expressed in this format, output a single line: ERROR: <reason>
        """;

    /// <summary>Builds a focused extraction prompt from <see cref="Preamble"/>, the requested construct sections, and <see cref="Rules"/>.</summary>
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
