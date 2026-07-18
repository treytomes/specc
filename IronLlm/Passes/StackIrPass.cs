using System.Diagnostics;
using System.Text.RegularExpressions;
using IronLlm.Graph;
using Microsoft.Extensions.Logging;

namespace IronLlm.Passes;

public class StackIrPass : ICompilerPass
{
    private readonly ILogger<StackIrPass> _logger;

    public StackIrPass(ILogger<StackIrPass> logger)
    {
        _logger = logger;
    }

    public string Name          => "05-StackIR";
    public string? ArtifactFile  => "05-stackir.json";

    public async Task LoadFromArtifactAsync(string artifactPath, CompilationContext context)
    {
        var json = await File.ReadAllTextAsync(artifactPath);
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        context.StackIr = System.Text.Json.JsonSerializer.Deserialize<List<StackInstruction>>(json, opts) ?? [];
    }

    public Task ExecuteAsync(CompilationContext context)
    {
        if (context.CfgBlocks.Count == 0)
            throw new InvalidOperationException("CfgBlocks not set");

        var sw = Stopwatch.StartNew();
        var ir = new List<StackInstruction>();

        foreach (var block in context.CfgBlocks)
        {
            ir.Add(new StackInstruction(OpCode.Label, block.Label));

            var blockOps = new List<StackInstruction>();
            // Track string variable names so print can emit the right Call overload.
            var stringVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prevInstr in ir)
                if (prevInstr.Op == OpCode.StlocStr && prevInstr.Operand != null)
                    stringVars.Add(prevInstr.Operand);
            foreach (var instr in block.Instructions)
            {
                var lowered = LowerInstruction(instr, stringVars).ToList();
                // Update stringVars with any new StlocStr emitted in this block.
                foreach (var si in lowered)
                    if (si.Op == OpCode.StlocStr && si.Operand != null)
                        stringVars.Add(si.Operand);
                if (lowered.Count == 0 && !string.IsNullOrWhiteSpace(instr))
                    _logger.LogWarning(
                        "StackIR: unrecognised instruction pattern in block {Block}: \"{Instruction}\"",
                        block.Label, instr);
                blockOps.AddRange(lowered);
            }

            ir.AddRange(blockOps);

            var lastOp = blockOps.LastOrDefault()?.Op;

            if (block.SuccessorFalse != null)
            {
                if (lastOp == OpCode.Cgt || lastOp == OpCode.Clt)
                    ir.Add(new StackInstruction(OpCode.Brtrue,  block.SuccessorFalse));
                else
                    ir.Add(new StackInstruction(OpCode.Brfalse, block.SuccessorFalse));

                if (block.SuccessorTrue != null)
                    ir.Add(new StackInstruction(OpCode.Br, block.SuccessorTrue));
            }
            else if (block.SuccessorTrue != null && block.Label != "exit")
            {
                ir.Add(new StackInstruction(OpCode.Br, block.SuccessorTrue));
            }

            _logger.LogDebug("Block {Label}: {Count} ops", block.Label, blockOps.Count);
        }

        ir.Add(new StackInstruction(OpCode.Ret));
        context.StackIr = ir;

        _logger.LogInformation("Pass {Name} completed in {ElapsedMs}ms — {Count} instructions",
            Name, sw.ElapsedMilliseconds, ir.Count);
        return Task.CompletedTask;
    }

    // Maps deterministic CFG instruction strings to stack ops.
    // CfgPass emits exact patterns; we match them precisely and extract values via regex.
    private static IEnumerable<StackInstruction> LowerInstruction(string instr, HashSet<string>? stringVars = null)
    {
        var s = instr.Trim();

        // "newarr {name} {size}" — allocate int array and store in named local
        // Must come before the generic init pattern.
        var newarrMatch = Regex.Match(s, @"^newarr\s+(\w+)\s+(\d+)$");
        if (newarrMatch.Success)
        {
            var arrName = newarrMatch.Groups[1].Value;
            var size    = newarrMatch.Groups[2].Value;
            yield return new(OpCode.LdcI4,  size);
            yield return new(OpCode.Newarr);
            yield return new(OpCode.StlocA, arrName);
            yield break;
        }

        // "arr[{idx}] = {value}" — store int constant into array element
        var arrInitMatch = Regex.Match(s, @"^(\w+)\[(\d+)\]\s*=\s*(-?\d+)$");
        if (arrInitMatch.Success)
        {
            var arrName = arrInitMatch.Groups[1].Value;
            var idx     = arrInitMatch.Groups[2].Value;
            var val     = arrInitMatch.Groups[3].Value;
            yield return new(OpCode.LdlocA,   arrName);
            yield return new(OpCode.LdcI4,    idx);
            yield return new(OpCode.LdcI4,    val);
            yield return new(OpCode.StelemI4);
            yield break;
        }

        // "{var} = {var2}" — variable-to-variable copy, e.g. "min_index = j"
        // Must come before the int-init pattern (which also matches "{var} = {int}").
        var varCopyMatch = Regex.Match(s, @"^(\w+)\s*=\s*([a-zA-Z_]\w*)$");
        if (varCopyMatch.Success)
        {
            yield return new(OpCode.LdlocS, varCopyMatch.Groups[2].Value);
            yield return new(OpCode.StlocS, varCopyMatch.Groups[1].Value);
            yield break;
        }

        // "{var} = {int}" — loop variable init, e.g. "n = 1"
        // Must not match "{var} = {var} + 1" (increment).
        var initMatch = Regex.Match(s, @"^(\w+)\s*=\s*(-?\d+)$");
        if (initMatch.Success)
        {
            yield return new(OpCode.LdcI4,  initMatch.Groups[2].Value);
            yield return new(OpCode.StlocS, initMatch.Groups[1].Value);
            yield break;
        }

        // "if {var} > {int} goto {label}" — loop termination test (flat loop)
        var loopTestMatch = Regex.Match(s, @"^if\s+(\w+)\s*>\s*(-?\d+)\s+goto\s+\w+$");
        if (loopTestMatch.Success)
        {
            yield return new(OpCode.LdlocS, loopTestMatch.Groups[1].Value);
            yield return new(OpCode.LdcI4,  loopTestMatch.Groups[2].Value);
            yield return new(OpCode.Cgt);
            yield break;
        }

        // "if {var} > ({int} - {var2}) goto {label}" — dynamic inner loop bound test
        var dynBoundMatch = Regex.Match(s, @"^if\s+(\w+)\s*>\s*\((\d+)\s*-\s*(\w+)\)\s+goto\s+\w+$");
        if (dynBoundMatch.Success)
        {
            yield return new(OpCode.LdlocS, dynBoundMatch.Groups[1].Value);   // j
            yield return new(OpCode.LdcI4,  dynBoundMatch.Groups[2].Value);   // outerBound
            yield return new(OpCode.LdlocS, dynBoundMatch.Groups[3].Value);   // i
            yield return new(OpCode.Sub);
            yield return new(OpCode.Cgt);
            yield break;
        }

        // "if {var} % {int} == 0" — divisibility check
        var modMatch = Regex.Match(s, @"^if\s+(\w+)\s*%\s*(-?\d+)\s*==\s*0$");
        if (modMatch.Success)
        {
            yield return new(OpCode.LdlocS, modMatch.Groups[1].Value);
            yield return new(OpCode.LdcI4,  modMatch.Groups[2].Value);
            yield return new(OpCode.Rem);
            yield return new(OpCode.LdcI4,  "0");
            yield return new(OpCode.Ceq);
            yield break;
        }

        // "if {arr}[{idx}] > {arr}[{idx}+1]" — array element comparison
        var arrCmpMatch = Regex.Match(s, @"^if\s+(\w+)\[(\w+)\]\s*>\s*\1\[(\w+)\+1\]$");
        if (arrCmpMatch.Success)
        {
            var arrName = arrCmpMatch.Groups[1].Value;
            var idxVar  = arrCmpMatch.Groups[2].Value;
            yield return new(OpCode.LdlocA,   arrName);
            yield return new(OpCode.LdlocS,   idxVar);
            yield return new(OpCode.LdelemI4);
            yield return new(OpCode.LdlocA,   arrName);
            yield return new(OpCode.LdlocS,   idxVar);
            yield return new(OpCode.LdcI4,    "1");
            yield return new(OpCode.Add);
            yield return new(OpCode.LdelemI4);
            yield return new(OpCode.Cgt);
            yield break;
        }

        // "if {arr}[{v1}] < {arr}[{v2}]" — element comparison with two variable indices
        var arrCmpVarMatch = Regex.Match(s, @"^if\s+(\w+)\[(\w+)\]\s*<\s*\1\[(\w+)\]$");
        if (arrCmpVarMatch.Success)
        {
            var arrName = arrCmpVarMatch.Groups[1].Value;
            var idx1    = arrCmpVarMatch.Groups[2].Value;
            var idx2    = arrCmpVarMatch.Groups[3].Value;
            // Push arr[idx2] > arr[idx1] (i.e. arr[idx1] < arr[idx2]) as Cgt.
            // StackIrPass emits Brtrue → SuccessorFalse when last op is Cgt.
            // CfgPass sets SuccessorFalse = "update_min" so: cgt=true → update_min.
            yield return new(OpCode.LdlocA,   arrName);
            yield return new(OpCode.LdlocS,   idx2);
            yield return new(OpCode.LdelemI4);
            yield return new(OpCode.LdlocA,   arrName);
            yield return new(OpCode.LdlocS,   idx1);
            yield return new(OpCode.LdelemI4);
            yield return new(OpCode.Cgt);
            yield break;
        }

        // "swap {arr}[{v1}] {arr}[{v2}]" — swap two elements with variable indices
        var swapVarMatch = Regex.Match(s, @"^swap\s+(\w+)\[(\w+)\]\s+\1\[(\w+)\]$");
        if (swapVarMatch.Success)
        {
            var arrName = swapVarMatch.Groups[1].Value;
            var idx1    = swapVarMatch.Groups[2].Value;
            var idx2    = swapVarMatch.Groups[3].Value;

            // temp = arr[idx1]
            yield return new(OpCode.LdlocA,   arrName);
            yield return new(OpCode.LdlocS,   idx1);
            yield return new(OpCode.LdelemI4);
            yield return new(OpCode.StlocS,   "temp");

            // arr[idx1] = arr[idx2]
            yield return new(OpCode.LdlocA,   arrName);
            yield return new(OpCode.LdlocS,   idx1);
            yield return new(OpCode.LdlocA,   arrName);
            yield return new(OpCode.LdlocS,   idx2);
            yield return new(OpCode.LdelemI4);
            yield return new(OpCode.StelemI4);

            // arr[idx2] = temp
            yield return new(OpCode.LdlocA,   arrName);
            yield return new(OpCode.LdlocS,   idx2);
            yield return new(OpCode.LdlocS,   "temp");
            yield return new(OpCode.StelemI4);
            yield break;
        }

        // "swap {arr}[{idx}] {arr}[{idx}+1]" — swap two adjacent array elements via temp
        var swapMatch = Regex.Match(s, @"^swap\s+(\w+)\[(\w+)\]\s+\1\[(\w+)\+1\]$");
        if (swapMatch.Success)
        {
            var arrName = swapMatch.Groups[1].Value;
            var idxVar  = swapMatch.Groups[2].Value;

            // temp = arr[j]
            yield return new(OpCode.LdlocA,   arrName);
            yield return new(OpCode.LdlocS,   idxVar);
            yield return new(OpCode.LdelemI4);
            yield return new(OpCode.StlocS,   "temp");

            // arr[j] = arr[j+1]
            yield return new(OpCode.LdlocA,   arrName);
            yield return new(OpCode.LdlocS,   idxVar);
            yield return new(OpCode.LdlocA,   arrName);
            yield return new(OpCode.LdlocS,   idxVar);
            yield return new(OpCode.LdcI4,    "1");
            yield return new(OpCode.Add);
            yield return new(OpCode.LdelemI4);
            yield return new(OpCode.StelemI4);

            // arr[j+1] = temp
            yield return new(OpCode.LdlocA,   arrName);
            yield return new(OpCode.LdlocS,   idxVar);
            yield return new(OpCode.LdcI4,    "1");
            yield return new(OpCode.Add);
            yield return new(OpCode.LdlocS,   "temp");
            yield return new(OpCode.StelemI4);
            yield break;
        }

        // "print {arr}[{idx}]" — print array element
        var printArrMatch = Regex.Match(s, @"^print\s+(\w+)\[(\w+)\]$");
        if (printArrMatch.Success)
        {
            var arrName = printArrMatch.Groups[1].Value;
            var idxVar  = printArrMatch.Groups[2].Value;
            yield return new(OpCode.LdlocA,    arrName);
            yield return new(OpCode.LdlocS,    idxVar);
            yield return new(OpCode.LdelemI4);
            yield return new(OpCode.Intrinsic, "console.write_line.int");
            yield break;
        }

        // "print {var}" — print a variable; string or int depending on type
        var printVarMatch = Regex.Match(s, @"^print\s+(\w+)$");
        if (printVarMatch.Success)
        {
            var varName = printVarMatch.Groups[1].Value;
            if (stringVars?.Contains(varName) == true)
            {
                yield return new(OpCode.LdlocStr,  varName);
                yield return new(OpCode.Intrinsic, "console.write_line.string");
            }
            else
            {
                yield return new(OpCode.LdlocS,    varName);
                yield return new(OpCode.Intrinsic, "console.write_line.int");
            }
            yield break;
        }

        // "print "...string..."" — print string literal
        var printStrMatch = Regex.Match(s, @"^print\s+""(.*)""$");
        if (printStrMatch.Success)
        {
            yield return new(OpCode.LdstrS,    printStrMatch.Groups[1].Value);
            yield return new(OpCode.Intrinsic, "console.write_line.string");
            yield break;
        }

        // `print_concat "prefix" varName "suffix"` — concat a literal prefix with a string
        // variable (and optional suffix), then println the result.
        // Suffix may be empty (""), in which case we skip the second concat.
        var printConcatMatch = Regex.Match(s, @"^print_concat\s+""(.*)""\s+(\w+)\s+""(.*)""$");
        if (printConcatMatch.Success)
        {
            var prefix  = printConcatMatch.Groups[1].Value;
            var varName = printConcatMatch.Groups[2].Value;
            var suffix  = printConcatMatch.Groups[3].Value;
            yield return new(OpCode.LdstrS,    prefix);
            yield return new(OpCode.LdlocStr,  varName);
            yield return new(OpCode.Intrinsic, "string.concat");
            if (!string.IsNullOrEmpty(suffix))
            {
                yield return new(OpCode.LdstrS,    suffix);
                yield return new(OpCode.Intrinsic, "string.concat");
            }
            yield return new(OpCode.Intrinsic, "console.write_line.string");
            yield break;
        }

        // "{var} = {var} + 1" — loop increment
        var incrMatch = Regex.Match(s, @"^(\w+)\s*=\s*(\w+)\s*\+\s*1$");
        if (incrMatch.Success)
        {
            yield return new(OpCode.LdlocS, incrMatch.Groups[2].Value);
            yield return new(OpCode.LdcI4,  "1");
            yield return new(OpCode.Add);
            yield return new(OpCode.StlocS, incrMatch.Groups[1].Value);
            yield break;
        }

        // "read {var}" — Console.ReadLine() → store in named string local
        var readMatch = Regex.Match(s, @"^read\s+(\w+)$");
        if (readMatch.Success)
        {
            yield return new(OpCode.Intrinsic, "console.read_line");
            yield return new(OpCode.StlocStr,  readMatch.Groups[1].Value);
            yield break;
        }

        // "read_int {var}" — Console.ReadLine() → int.Parse() → store in named int local
        var readIntMatch = Regex.Match(s, @"^read_int\s+(\w+)$");
        if (readIntMatch.Success)
        {
            yield return new(OpCode.Intrinsic, "console.read_line");
            yield return new(OpCode.Intrinsic, "int.parse");
            yield return new(OpCode.StlocS,    readIntMatch.Groups[1].Value);
            yield break;
        }

        // "rand_int {name} {min} {max}" — Random.Shared.Next(min, max+1) → store
        var randIntMatch = Regex.Match(s, @"^rand_int\s+(\w+)\s+(-?\d+)\s+(-?\d+)$");
        if (randIntMatch.Success)
        {
            var name = randIntMatch.Groups[1].Value;
            var min  = randIntMatch.Groups[2].Value;
            var max  = (int.Parse(randIntMatch.Groups[3].Value) + 1).ToString();
            yield return new(OpCode.RandInt, $"{name}:{min}:{max}");
            yield break;
        }

        // "if {var} lt {var}" — less-than var-vs-var comparison
        var cmpLtVarMatch = Regex.Match(s, @"^if\s+(\w+)\s+lt\s+\{(\w+)\}$");
        if (cmpLtVarMatch.Success)
        {
            yield return new(OpCode.LdlocS, cmpLtVarMatch.Groups[1].Value);
            yield return new(OpCode.LdlocS, cmpLtVarMatch.Groups[2].Value);
            yield return new(OpCode.Clt);
            yield break;
        }

        // "if {var} gt {var}" — greater-than var-vs-var comparison
        var cmpGtVarMatch = Regex.Match(s, @"^if\s+(\w+)\s+gt\s+\{(\w+)\}$");
        if (cmpGtVarMatch.Success)
        {
            yield return new(OpCode.LdlocS, cmpGtVarMatch.Groups[1].Value);
            yield return new(OpCode.LdlocS, cmpGtVarMatch.Groups[2].Value);
            yield return new(OpCode.Cgt);
            yield break;
        }

        // "if {var} eq {var}" — equality var-vs-var comparison
        var cmpEqVarMatch = Regex.Match(s, @"^if\s+(\w+)\s+eq\s+\{(\w+)\}$");
        if (cmpEqVarMatch.Success)
        {
            yield return new(OpCode.LdlocS, cmpEqVarMatch.Groups[1].Value);
            yield return new(OpCode.LdlocS, cmpEqVarMatch.Groups[2].Value);
            yield return new(OpCode.Ceq);
            yield break;
        }

        // "if {var} ne {var}" — not-equal var-vs-var comparison (Ceq + invert)
        var cmpNeVarMatch = Regex.Match(s, @"^if\s+(\w+)\s+ne\s+\{(\w+)\}$");
        if (cmpNeVarMatch.Success)
        {
            yield return new(OpCode.LdlocS, cmpNeVarMatch.Groups[1].Value);
            yield return new(OpCode.LdlocS, cmpNeVarMatch.Groups[2].Value);
            yield return new(OpCode.Ceq);
            yield return new(OpCode.LdcI4, "0");
            yield return new(OpCode.Ceq);
            yield break;
        }

        // "if {var} lt {int}" — less-than comparison
        var cmpLtMatch = Regex.Match(s, @"^if\s+(\w+)\s+lt\s+(-?\d+)$");
        if (cmpLtMatch.Success)
        {
            yield return new(OpCode.LdlocS, cmpLtMatch.Groups[1].Value);
            yield return new(OpCode.LdcI4,  cmpLtMatch.Groups[2].Value);
            yield return new(OpCode.Clt);
            yield break;
        }

        // "if {var} gt {int}" — greater-than comparison
        var cmpGtMatch = Regex.Match(s, @"^if\s+(\w+)\s+gt\s+(-?\d+)$");
        if (cmpGtMatch.Success)
        {
            yield return new(OpCode.LdlocS, cmpGtMatch.Groups[1].Value);
            yield return new(OpCode.LdcI4,  cmpGtMatch.Groups[2].Value);
            yield return new(OpCode.Cgt);
            yield break;
        }

        // "if {var} eq {int}" — equality comparison
        var cmpEqMatch = Regex.Match(s, @"^if\s+(\w+)\s+eq\s+(-?\d+)$");
        if (cmpEqMatch.Success)
        {
            yield return new(OpCode.LdlocS, cmpEqMatch.Groups[1].Value);
            yield return new(OpCode.LdcI4,  cmpEqMatch.Groups[2].Value);
            yield return new(OpCode.Ceq);
            yield break;
        }

        // "if {var} ne {int}" — not-equal comparison (Ceq + invert)
        var cmpNeMatch = Regex.Match(s, @"^if\s+(\w+)\s+ne\s+(-?\d+)$");
        if (cmpNeMatch.Success)
        {
            yield return new(OpCode.LdlocS, cmpNeMatch.Groups[1].Value);
            yield return new(OpCode.LdcI4,  cmpNeMatch.Groups[2].Value);
            yield return new(OpCode.Ceq);
            yield return new(OpCode.LdcI4,  "0");
            yield return new(OpCode.Ceq);
            yield break;
        }

        // "print {var}" where the variable is a string local — detected by StlocStr history;
        // handled identically to int print at this level; MsilGenerationPass distinguishes by type.
        // (No change needed here — the existing printVarMatch handles it; type is resolved downstream.)

        // "assign {target} copy {left}" — variable copy (no right operand)
        var assignCopyMatch = Regex.Match(s, @"^assign\s+(\w+)\s+copy\s+(\{?\w+\}?)$");
        if (assignCopyMatch.Success)
        {
            var target = assignCopyMatch.Groups[1].Value;
            var lRaw   = assignCopyMatch.Groups[2].Value;
            var name   = lRaw.Trim('{', '}');
            if (int.TryParse(name, out var cv))
                yield return new(OpCode.LdcI4, cv.ToString());
            else
                yield return new(OpCode.LdlocS, name);
            yield return new(OpCode.StlocS, target);
            yield break;
        }

        // "assign {target} {op} {left} {right}" — arithmetic assignment
        // left/right are either {varName} (variable reference) or bare integer literals.
        var assignMatch = Regex.Match(s, @"^assign\s+(\w+)\s+(mul|add|sub|div)\s+(\{?\w+\}?)\s+(\{?\w+\}?)$");
        if (assignMatch.Success)
        {
            var target = assignMatch.Groups[1].Value;
            var op     = assignMatch.Groups[2].Value;
            var lRaw   = assignMatch.Groups[3].Value;
            var rRaw   = assignMatch.Groups[4].Value;

            static IEnumerable<StackInstruction> LoadOperand(string raw)
            {
                var name = raw.Trim('{', '}');
                if (int.TryParse(name, out var v))
                    yield return new(OpCode.LdcI4, v.ToString());
                else
                    yield return new(OpCode.LdlocS, name);
            }

            foreach (var i in LoadOperand(lRaw)) yield return i;
            foreach (var i in LoadOperand(rRaw)) yield return i;
            yield return op switch
            {
                "mul" => new(OpCode.Mul),
                "sub" => new(OpCode.Sub),
                "div" => new(OpCode.Div),
                _     => new(OpCode.Add),
            };
            yield return new(OpCode.StlocS, target);
            yield break;
        }
    }
}
