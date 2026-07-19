using System.Diagnostics;
using System.Text;
using Specc.Graph;
using Microsoft.Extensions.Logging;

namespace Specc.Passes;

/// <summary>Produces a Mermaid flowchart and SVG diagram of the CFG basic blocks.</summary>
public class CfgVisualizationPass : ICompilerPass
{
    private readonly ILogger<CfgVisualizationPass> _logger;

    /// <summary>Initialises the pass with a logger.</summary>
    public CfgVisualizationPass(ILogger<CfgVisualizationPass> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public string  Name         => "04b-CfgVisualization";
    /// <inheritdoc/>
    public string? ArtifactFile => "04b-cfg-flowchart.mmd";

    /// <inheritdoc/>
    public Task LoadFromArtifactAsync(string artifactPath, CompilationContext context) =>
        Task.CompletedTask;

    /// <inheritdoc/>
    public async Task ExecuteAsync(CompilationContext context)
    {
        if (context.CfgBlocks.Count == 0)
            throw new InvalidOperationException("CfgBlocks not set");

        var sw = Stopwatch.StartNew();

        var mmd = BuildMermaid(context.CfgBlocks);
        var svg = BuildSvg(context.CfgBlocks);

        Directory.CreateDirectory(context.ArtifactsDir);
        var mmdPath = Path.Combine(context.ArtifactsDir, ArtifactFile!);
        var svgPath = Path.Combine(context.ArtifactsDir, "04c-cfg-flowchart.svg");

        await File.WriteAllTextAsync(mmdPath, mmd);
        await File.WriteAllTextAsync(svgPath, svg);

        _logger.LogInformation(
            "Pass {Name} completed in {ElapsedMs}ms → {Mmd}, {Svg}",
            Name, sw.ElapsedMilliseconds, mmdPath, svgPath);
    }

    // ── Mermaid ───────────────────────────────────────────────────────────────

    /// <summary>Renders the given CFG blocks as a Mermaid flowchart string.</summary>
    public static string BuildMermaid(IReadOnlyList<CfgBlock> blocks)
    {
        var indexMap = blocks
            .Select((b, i) => (b.Label, i))
            .ToDictionary(x => x.Label, x => x.i);

        var sb = new StringBuilder();
        sb.AppendLine("flowchart TD");

        // Node declarations
        foreach (var block in blocks)
        {
            var id    = MmdId(block.Label);
            var lines = new List<string> { block.Label };
            lines.AddRange(block.Instructions.Select(EscapeMmd));
            var content = string.Join("<br/>", lines);

            string decl;
            if (block.Label is "entry" or "exit")
                decl = $"  {id}([\"{content}\"])";
            else if (IsConditionBlock(block))
                decl = $"  {id}{{\"{content}\"}}";
            else
                decl = $"  {id}[\"{content}\"]";

            sb.AppendLine(decl);
        }

        sb.AppendLine();

        // Edge declarations
        foreach (var block in blocks)
        {
            var fromId   = MmdId(block.Label);
            var hasBoth  = block.SuccessorTrue != null && block.SuccessorFalse != null;

            if (block.SuccessorTrue != null)
            {
                var toId  = MmdId(block.SuccessorTrue);
                var arrow = hasBoth ? $"-->|yes|" : "-->";
                sb.AppendLine($"  {fromId} {arrow} {toId}");
            }

            if (block.SuccessorFalse != null)
            {
                var toId = MmdId(block.SuccessorFalse);
                sb.AppendLine($"  {fromId} -->|no| {toId}");
            }
        }

        return sb.ToString();
    }

    // A block is a condition block when its only instruction is an "if …" test.
    private static bool IsConditionBlock(CfgBlock block) =>
        block.Instructions.Count == 1 &&
        block.Instructions[0].TrimStart().StartsWith("if ", StringComparison.Ordinal);

    private static string MmdId(string label) =>
        System.Text.RegularExpressions.Regex.Replace(label, @"[^A-Za-z0-9]", "_");

    private static string EscapeMmd(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // ── SVG ───────────────────────────────────────────────────────────────────

    /// <summary>Renders the given CFG blocks as a layered SVG diagram string.</summary>
    public static string BuildSvg(IReadOnlyList<CfgBlock> blocks)
    {
        const int NodeW    = 240;
        const int LineH    = 18;
        const int PadV     = 8;
        const int HGap     = 50;
        const int VGap     = 50;
        const int Padding  = 40;
        const int BackArcX = 30; // how far to the left back-edge arcs bow out

        var indexMap = blocks
            .Select((b, i) => (b.Label, i))
            .ToDictionary(x => x.Label, x => x.i);

        // Height of each block = header line + instruction lines, with padding
        int BlockH(CfgBlock b) => PadV * 2 + LineH * (1 + b.Instructions.Count);

        // BFS rank assignment using SuccessorTrue only (keeps dominant path vertical).
        var ranks = new Dictionary<string, int>();
        var queue = new Queue<string>();
        if (blocks.Count > 0)
        {
            ranks[blocks[0].Label] = 0;
            queue.Enqueue(blocks[0].Label);
        }
        var blockMap = blocks.ToDictionary(b => b.Label);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!blockMap.TryGetValue(cur, out var blk)) continue;
            if (blk.SuccessorTrue != null && !ranks.ContainsKey(blk.SuccessorTrue))
            {
                ranks[blk.SuccessorTrue] = ranks[cur] + 1;
                queue.Enqueue(blk.SuccessorTrue);
            }
            if (blk.SuccessorFalse != null && !ranks.ContainsKey(blk.SuccessorFalse))
            {
                ranks[blk.SuccessorFalse] = ranks[cur] + 1;
                queue.Enqueue(blk.SuccessorFalse);
            }
        }
        foreach (var b in blocks)
            ranks.TryAdd(b.Label, 0);

        // Position blocks by rank row
        var byRank = ranks
            .GroupBy(kv => kv.Value)
            .OrderBy(g => g.Key)
            .Select(g => g.Select(kv => kv.Key).ToList())
            .ToList();

        var pos = new Dictionary<string, (float X, float Y)>();
        float yOffset = Padding;
        foreach (var row in byRank)
        {
            var rowW = row.Count * NodeW + (row.Count - 1) * HGap;
            for (var col = 0; col < row.Count; col++)
            {
                var label = row[col];
                var x = Padding + col * (NodeW + HGap);
                pos[label] = (x, yOffset);
            }
            // Row height = tallest block in the row
            var rowH = row.Max(l => blockMap.TryGetValue(l, out var b) ? BlockH(b) : LineH + PadV * 2);
            yOffset += rowH + VGap;
        }

        // Centre each row
        var maxRowW = byRank.Max(row => row.Count * NodeW + (row.Count - 1) * HGap);
        foreach (var row in byRank)
        {
            var rowW = row.Count * NodeW + (row.Count - 1) * HGap;
            var shift = (maxRowW - rowW) / 2f;
            foreach (var label in row)
                pos[label] = (pos[label].X + shift, pos[label].Y);
        }

        var canvasW = (int)(Padding * 2 + maxRowW + BackArcX + 20);
        var canvasH = (int)(yOffset + Padding);

        var sb = new StringBuilder();
        sb.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" width="{canvasW}" height="{canvasH}" viewBox="0 0 {canvasW} {canvasH}">""");
        sb.AppendLine("""  <defs>""");
        sb.AppendLine("""    <marker id="arr"  markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto"><path d="M0,0 L0,6 L8,3 z" fill="#555"/></marker>""");
        sb.AppendLine("""    <marker id="arrG" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto"><path d="M0,0 L0,6 L8,3 z" fill="#3a3"/></marker>""");
        sb.AppendLine("""    <marker id="arrA" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto"><path d="M0,0 L0,6 L8,3 z" fill="#c70"/></marker>""");
        sb.AppendLine("""  </defs>""");

        // Draw edges first (under blocks)
        foreach (var block in blocks)
        {
            if (!pos.TryGetValue(block.Label, out var fp)) continue;
            var fh = BlockH(block);
            var fx = fp.X + NodeW / 2f;
            var fy = fp.Y + fh;

            void DrawEdge(string target, string color, string markerId, string? edgeLabel)
            {
                if (!pos.TryGetValue(target, out var tp)) return;
                var th = blockMap.TryGetValue(target, out var tb) ? BlockH(tb) : fh;
                var tx = tp.X + NodeW / 2f;
                var ty = tp.Y;

                var isBack = indexMap.TryGetValue(target, out var ti) &&
                             indexMap.TryGetValue(block.Label, out var fi) &&
                             ti <= fi;

                string pathD;
                if (isBack)
                {
                    // Arc bowing left of the diagram
                    var leftX = Padding - BackArcX;
                    var midY  = (fp.Y + fh + tp.Y) / 2f;
                    pathD = $"M {fx:F0},{fy:F0} C {leftX:F0},{fy + 20:F0} {leftX:F0},{ty - 20:F0} {tx:F0},{ty:F0}";
                    sb.AppendLine($"""  <path d="{pathD}" style="stroke:{color};fill:none;stroke-width:1.5;stroke-dasharray:5;marker-end:url(#{markerId})"/>""");
                }
                else
                {
                    pathD = $"M {fx:F0},{fy:F0} L {tx:F0},{ty:F0}";
                    sb.AppendLine($"""  <path d="{pathD}" style="stroke:{color};fill:none;stroke-width:1.5;marker-end:url(#{markerId})"/>""");
                }

                if (edgeLabel != null)
                {
                    var lx = (fx + tx) / 2f + 6;
                    var ly = (fy + ty) / 2f - 4;
                    sb.AppendLine($"""  <text x="{lx:F0}" y="{ly:F0}" font-size="10" fill="{color}" font-family="monospace">{EscapeXml(edgeLabel)}</text>""");
                }
            }

            var hasBoth = block.SuccessorTrue != null && block.SuccessorFalse != null;
            if (block.SuccessorTrue != null)
                DrawEdge(block.SuccessorTrue, "#3a3", "arrG", hasBoth ? "yes" : null);
            if (block.SuccessorFalse != null)
                DrawEdge(block.SuccessorFalse, "#c70", "arrA", "no");
        }

        // Draw blocks
        foreach (var block in blocks)
        {
            if (!pos.TryGetValue(block.Label, out var p)) continue;
            var bh   = BlockH(block);
            var fill = BlockColor(block);

            sb.AppendLine($"""  <rect x="{p.X:F0}" y="{p.Y:F0}" width="{NodeW}" height="{bh}" rx="5" style="fill:{fill};stroke:#666;stroke-width:1.2"/>""");

            // Label line (bold)
            var labelY = p.Y + PadV + LineH - 4;
            sb.AppendLine($"""  <text x="{p.X + NodeW / 2f:F0}" y="{labelY:F0}" text-anchor="middle" font-size="12" font-weight="bold" font-family="monospace" fill="#222">{EscapeXml(block.Label)}</text>""");

            // Instruction lines
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instrY = labelY + (i + 1) * LineH;
                var instr  = block.Instructions[i];
                if (instr.Length > 36) instr = instr[..35] + "…";
                sb.AppendLine($"""  <text x="{p.X + 8:F0}" y="{instrY:F0}" font-size="10" font-family="monospace" fill="#444">{EscapeXml(instr)}</text>""");
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string BlockColor(CfgBlock block)
    {
        if (block.Label is "entry" or "exit") return "#4a90d9";
        if (IsConditionBlock(block))          return "#f5a623";
        if (block.Instructions.Any(i => i.StartsWith("print ", StringComparison.Ordinal)))
            return "#7ed321";
        return "#f0f0f0";
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
