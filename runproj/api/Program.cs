using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// request record moved below top-level statements to satisfy compiler
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors();
var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// Serve static files from parent folder (runproj) so http://localhost:5000/Train.html works
var parentFolder = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".."));
var parentProvider = new PhysicalFileProvider(parentFolder);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = parentProvider });
app.UseStaticFiles(new StaticFileOptions { FileProvider = parentProvider });

// siding lengths (same topology as your console Program.cs)
var sidingLengths = new Dictionary<string,int> { ["A"]=2, ["B"]=3, ["C"]=1, ["D"]=2, ["E"]=1 };

// build graph helper
Dictionary<string, List<string>> BuildGraph()
{
    var graph = new Dictionary<string, List<string>>();
    foreach (var kv in sidingLengths)
    {
        var nodes = Enumerable.Range(0, kv.Value).Select(i => $"{kv.Key}{i}").ToList();
        for (int i = 0; i < nodes.Count; i++)
        {
            if (!graph.ContainsKey(nodes[i])) graph[nodes[i]] = new List<string>();
            if (i > 0) graph[nodes[i]].Add(nodes[i - 1]);
            if (i < nodes.Count - 1) graph[nodes[i]].Add(nodes[i + 1]);
        }
    }

    graph["MAIN"] = new List<string>();
    foreach (var kv in sidingLengths)
    {
        var tail = $"{kv.Key}{kv.Value - 1}";
        graph["MAIN"].Add(tail);
        graph[tail].Add("MAIN");
    }

    graph["M1"] = new List<string> { "MAIN" };
    graph["M2"] = new List<string> { "MAIN" };
    graph["MAIN"].Add("M1"); graph["MAIN"].Add("M2");

    return graph;
}

// BFS shortest path
List<string> ShortestPath(Dictionary<string, List<string>> graph, string from, string to)
{
    if (!graph.ContainsKey(from) || !graph.ContainsKey(to)) return new List<string>();
    var q = new Queue<string>();
    var parent = new Dictionary<string, string?>();
    q.Enqueue(from);
    parent[from] = null;
    while (q.Count > 0)
    {
        var u = q.Dequeue();
        if (u == to) break;
        foreach (var v in graph[u])
        {
            if (!parent.ContainsKey(v))
            {
                parent[v] = u;
                q.Enqueue(v);
            }
        }
    }
    if (!parent.ContainsKey(to)) return new List<string>();
    var path = new List<string>();
    string? cur = to;
    while (cur != null)
    {
        path.Add(cur);
        cur = parent[cur];
    }
    path.Reverse();
    return path;
}

app.MapGet("/stops", () =>
{
    var graph = BuildGraph();

    // coupling stations = nodes directly connected to MAIN
    var coupling = graph.ContainsKey("MAIN")
        ? graph["MAIN"].Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList()
        : new List<string>();

    // compute degrees and pick nodes with degree == 1 as endstations
    var endstations = graph
        .Where(kv => kv.Value.Count == 1)
        .Select(kv => kv.Key)
        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
        .ToList();

    return Results.Json(new { endstations = endstations, couplingStations = coupling });
});

app.MapPost("/route", (RouteRequest req) =>
{
    var graph = BuildGraph();
    var raw = (req.input ?? "").Trim().ToUpper();
    var tokens = string.IsNullOrWhiteSpace(raw)
        ? new List<string>()
        : raw.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim().ToUpper()).ToList();

    // prefer non-loco tokens (so "M1 MAIN A1" picks A1)
    string? dest = null;
    var nonLoc = tokens.Where(t => t != "M1" && t != "MAIN" && t != "M2").ToList();

    foreach (var t in nonLoc)
    {
        if (t.Length == 1 && sidingLengths.ContainsKey(t))
        {
            dest = $"{t}{sidingLengths[t] - 1}";
            break;
        }
    }
    if (dest == null)
    {
        foreach (var t in nonLoc)
        {
            if (graph.ContainsKey(t)) { dest = t; break; }
        }
    }
    if (dest == null)
    {
        foreach (var t in tokens)
        {
            if (t.Length == 1 && sidingLengths.ContainsKey(t)) { dest = $"{t}{sidingLengths[t] - 1}"; break; }
            if (graph.ContainsKey(t)) { dest = t; break; }
        }
    }

    if (dest == null) return Results.Json(new { error = "No destination found", path = new string[0], moves = new string[0] });

    var path = ShortestPath(graph, "M1", dest);
    if (path.Count == 0) return Results.Json(new { error = $"No path M1 -> {dest}", path = new string[0], moves = new string[0] });

    var moves = new List<string>();
    for (int i = 1; i < path.Count; i++) moves.Add($"{path[i-1]}->{path[i]}");

    return Results.Json(new { path = path, moves = moves });
});

app.Run("http://localhost:5000");

record RouteRequest(string input);
