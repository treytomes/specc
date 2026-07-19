using System.Diagnostics;
using System.Text;
using Specc.Graph;
using Microsoft.Extensions.Logging;

namespace Specc.Passes;

/// <summary>Produces a Mermaid flowchart and SVG diagram of the semantic graph.</summary>
public class GraphVisualizationPass : ICompilerPass
{
    private readonly ILogger<GraphVisualizationPass> _logger;

    /// <summary>Initialises the pass with a logger.</summary>
    public GraphVisualizationPass(ILogger<GraphVisualizationPass> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public string  Name         => "02b-GraphVisualization";
    /// <inheritdoc/>
    public string? ArtifactFile => "02b-semantic-graph.mmd";

    /// <inheritdoc/>
    public Task LoadFromArtifactAsync(string artifactPath, CompilationContext context) =>
        Task.CompletedTask;

    /// <inheritdoc/>
    public async Task ExecuteAsync(CompilationContext context)
    {
        var graph = context.SemanticGraph
            ?? throw new InvalidOperationException("SemanticGraph not set");

        var sw = Stopwatch.StartNew();

        var mmd = BuildMermaid(graph.Nodes, graph.Edges);
        var svg = BuildSvg(graph.Nodes, graph.Edges);

        Directory.CreateDirectory(context.ArtifactsDir);

        var mmdPath = Path.Combine(context.ArtifactsDir, ArtifactFile!);
        var svgPath = Path.Combine(context.ArtifactsDir, "02c-semantic-graph.svg");

        await File.WriteAllTextAsync(mmdPath, mmd);
        await File.WriteAllTextAsync(svgPath, svg);

        _logger.LogInformation(
            "Pass {Name} completed in {ElapsedMs}ms → {Mmd}, {Svg}",
            Name, sw.ElapsedMilliseconds, mmdPath, svgPath);
    }

    // ── Mermaid ───────────────────────────────────────────────────────────────

    /// <summary>Renders the semantic graph as a Mermaid flowchart string, excluding assertion nodes.</summary>
    public static string BuildMermaid(IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges)
    {
        nodes = nodes.Where(n => n is not AssertionNode).ToList();
        var nodeIds = nodes.Select(n => n.Id).ToHashSet();
        edges = edges.Where(e => nodeIds.Contains(e.From) && nodeIds.Contains(e.To)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("flowchart TD");

        foreach (var node in nodes)
        {
            var id    = MermaidId(node);
            var label = node.Label.Replace("\"", "'");
            sb.AppendLine($"  {id}[\"{label}\"]");
        }

        var idMap = nodes.ToDictionary(n => n.Id, MermaidId);
        foreach (var edge in edges)
        {
            if (!idMap.TryGetValue(edge.From, out var from) ||
                !idMap.TryGetValue(edge.To,   out var to))
                continue;

            var arrow = edge.Type switch
            {
                EdgeType.TrueBranch  => "-- TrueBranch -->",
                EdgeType.FalseBranch => "-- FalseBranch -->",
                EdgeType.DependsOn   => "-. DependsOn .->",
                EdgeType.Asserts     => "-- Asserts -->",
                _                    => $"-- {edge.Type} -->",
            };
            sb.AppendLine($"  {from} {arrow} {to}");
        }

        return sb.ToString();
    }

    private static string MermaidId(Node node) =>
        System.Text.RegularExpressions.Regex.Replace(node.Label, @"[^A-Za-z0-9]", "_");

    // ── SVG ───────────────────────────────────────────────────────────────────

    /// <summary>Renders the semantic graph as a layered SVG diagram string, excluding assertion nodes.</summary>
    public static string BuildSvg(IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges)
    {
        nodes = nodes.Where(n => n is not AssertionNode).ToList();
        var filteredIds = nodes.Select(n => n.Id).ToHashSet();
        edges = edges.Where(e => filteredIds.Contains(e.From) && filteredIds.Contains(e.To)).ToList();

        const int NodeW   = 160;
        const int NodeH   = 36;
        const int HGap    = 40;
        const int VGap    = 60;
        const int Padding = 20;

        // BFS rank assignment from ProgramNode (or first node).
        var ranks = AssignRanks(nodes, edges);
        var byRank = ranks
            .GroupBy(kv => kv.Value)
            .OrderBy(g => g.Key)
            .Select(g => g.Select(kv => kv.Key).ToList())
            .ToList();

        // Position each node.
        var pos = new Dictionary<Guid, (float X, float Y)>();
        for (var row = 0; row < byRank.Count; row++)
        {
            var rowNodes = byRank[row];
            var rowWidth = rowNodes.Count * NodeW + (rowNodes.Count - 1) * HGap;
            for (var col = 0; col < rowNodes.Count; col++)
            {
                var x = Padding + col * (NodeW + HGap) - rowWidth / 2f;
                var y = Padding + row * (NodeH + VGap);
                pos[rowNodes[col]] = (x, y);
            }
        }

        // Canvas size.
        var allX = pos.Values.Select(p => p.X + NodeW).Append(0f).Max();
        var allY = pos.Values.Select(p => p.Y + NodeH).Append(0f).Max();
        var width  = (int)(allX + Padding * 2);
        var height = (int)(allY + Padding * 2);

        // Centre all nodes horizontally.
        var minX = pos.Values.Select(p => p.X).Min();
        var shift = Padding - minX;
        pos = pos.ToDictionary(kv => kv.Key, kv => (kv.Value.X + shift, kv.Value.Y));
        width = (int)(width + shift);

        var nodeMap = nodes.ToDictionary(n => n.Id);
        var sb = new StringBuilder();
        sb.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">""");
        sb.AppendLine("""  <defs><marker id="arrow" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0,0 L0,6 L8,3 z" fill="#555"/></marker></defs>""");

        // Edges first (drawn under nodes).
        foreach (var edge in edges)
        {
            if (!pos.TryGetValue(edge.From, out var fp) || !pos.TryGetValue(edge.To, out var tp))
                continue;

            var x1 = fp.X + NodeW / 2f;
            var y1 = fp.Y + NodeH;
            var x2 = tp.X + NodeW / 2f;
            var y2 = tp.Y;

            var (stroke, dash) = edge.Type switch
            {
                EdgeType.TrueBranch  => ("#3a3", ""),
                EdgeType.FalseBranch => ("#c33", "stroke-dasharray:4"),
                EdgeType.DependsOn   => ("#999", "stroke-dasharray:2"),
                _                    => ("#555", ""),
            };

            var style = $"stroke:{stroke};fill:none;stroke-width:1.5;marker-end:url(#arrow);{dash}";
            var label = edge.Type.ToString();
            var mx = (x1 + x2) / 2f;
            var my = (y1 + y2) / 2f;

            sb.AppendLine($"""  <line x1="{x1:F0}" y1="{y1:F0}" x2="{x2:F0}" y2="{y2:F0}" style="{style}"/>""");
            sb.AppendLine($"""  <text x="{mx:F0}" y="{my:F0}" text-anchor="middle" font-size="9" fill="#666">{label}</text>""");
        }

        // Nodes.
        foreach (var node in nodes)
        {
            if (!pos.TryGetValue(node.Id, out var p)) continue;
            var fill = NodeColor(node);
            var text = node.Label.Length > 22 ? node.Label[..22] + "…" : node.Label;
            sb.AppendLine($"""  <rect x="{p.X:F0}" y="{p.Y:F0}" width="{NodeW}" height="{NodeH}" rx="4" style="fill:{fill};stroke:#666;stroke-width:1"/>""");
            sb.AppendLine($"""  <text x="{p.X + NodeW / 2f:F0}" y="{p.Y + NodeH / 2f + 4:F0}" text-anchor="middle" font-size="11" font-family="monospace" fill="#222">{EscapeXml(text)}</text>""");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static Dictionary<Guid, int> AssignRanks(IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges)
    {
        var ranks   = new Dictionary<Guid, int>();
        var program = nodes.FirstOrDefault(n => n is ProgramNode) ?? nodes.FirstOrDefault();
        if (program == null) return ranks;

        var adj = edges
            .Where(e => e.Type != EdgeType.FalseBranch)
            .GroupBy(e => e.From)
            .ToDictionary(g => g.Key, g => g.Select(e => e.To).ToList());

        var queue = new Queue<Guid>();
        queue.Enqueue(program.Id);
        ranks[program.Id] = 0;

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!adj.TryGetValue(cur, out var children)) continue;
            foreach (var child in children)
            {
                if (!ranks.ContainsKey(child))
                {
                    ranks[child] = ranks[cur] + 1;
                    queue.Enqueue(child);
                }
            }
        }

        // Assign rank 0 to any disconnected nodes.
        foreach (var node in nodes)
            ranks.TryAdd(node.Id, 0);

        return ranks;
    }

    private static string NodeColor(Node node) => node switch
    {
        ProgramNode    => "#4a90d9",
        LoopNode       => "#7b68ee",
        BranchNode     => "#f5a623",
        ModuloNode     => "#e8e8e8",
        PrintNode      => "#7ed321",
        VariableNode   => "#d0d0d0",
        ConstantNode   => "#d0d0d0",
        ComparisonNode => "#d0d0d0",
        ArrayNode      => "#d4a843",
        IndexNode      => "#c4a4e0",
        SwapNode       => "#e08080",
        NestedLoopNode => "#7ab8c4",
        _              => "#ffffff",
    };

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
