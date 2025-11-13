using System;
using System.Collections.Generic;
using System.Linq;

namespace TimesaverSolverCSharp
{
    class Program
    {
        public record State(string Loco, IReadOnlyList<string> Attached, IReadOnlyDictionary<string, string> Cars);

        public record ActionStep(string Action, State State);

        static void Main()
        {
            var sidingLengths = new Dictionary<string, int>
            {
                ["A"] = 2,
                ["B"] = 3,
                ["C"] = 1,
                ["D"] = 2,
                ["E"] = 1
            };

            var graph = BuildTimesaverLayout(sidingLengths);

            var startCars = new Dictionary<string, string>
            {
                ["C1"] = "A0",
                ["C2"] = "B1",
                ["C3"] = "D0",
                ["C4"] = "E0"
            };

            var carGoals = new Dictionary<string, HashSet<string>>
            {
                ["C1"] = new HashSet<string> { "E0" },
                ["C2"] = new HashSet<string> { "C0" },
                ["C3"] = new HashSet<string> { "B2" },
                ["C4"] = new HashSet<string> { "A1" }
            };

            // quick reachability check (ignores blocking by other cars)
            Dictionary<string, int> BFSdist(string start, Dictionary<string, List<string>> g)
            {
                var dist = g.Keys.ToDictionary(k => k, _ => int.MaxValue);
                var q = new Queue<string>();
                dist[start] = 0;
                q.Enqueue(start);
                while (q.Count > 0)
                {
                    var u = q.Dequeue();
                    foreach (var v in g[u])
                    {
                        if (dist[v] == int.MaxValue)
                        {
                            dist[v] = dist[u] + 1;
                            q.Enqueue(v);
                        }
                    }
                }
                return dist;
            }

            Console.WriteLine("Reachability (ignoring blocking):");
            foreach (var kv in startCars)
            {
                var car = kv.Key;
                var startNode = kv.Value;
                if (!carGoals.TryGetValue(car, out var goals)) goals = new HashSet<string> { startNode };
                var dist = BFSdist(startNode, graph);
                var dists = goals.Select(g => (g, d: dist.ContainsKey(g) ? dist[g] : int.MaxValue)).ToList();
                var min = dists.Min(x => x.d);
                Console.WriteLine($" {car} from {startNode} -> goals [{string.Join(", ", dists.Select(x => $"{x.g}:{(x.d == int.MaxValue ? "inf" : x.d.ToString())}"))}]  min={(min == int.MaxValue ? "inf" : min.ToString())}");
            }
            Console.WriteLine();

            var solver = new TimesaverSolver(graph, carGoals);
            var startState = new State("M1", new List<string>(), new Dictionary<string, string>(startCars));

            var solution = solver.Solve(startState, maxSteps: 1_000_000_000);

            if (solution == null)
            {
                Console.WriteLine("No solution found (within step limit).");
            }
            else
            {
                Console.WriteLine($"Solution found in {solution.Count} actions:\n");
                int idx = 1;
                foreach (var step in solution)
                {
                    var attached = step.State.Attached != null && step.State.Attached.Count > 0
                        ? string.Join(", ", step.State.Attached)
                        : "(none)";
                    var cars = step.State.Cars != null && step.State.Cars.Count > 0
                        ? string.Join(", ", step.State.Cars.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"))
                        : "(none)";
                    Console.WriteLine($"{idx++}: {step.Action} -> Loco={step.State.Loco}, Attached=[{attached}], Cars={cars}");
                }
                Console.WriteLine();
            }
        }
        

        public class TimesaverSolver
        {
            private readonly Dictionary<string, List<string>> graph;
            private readonly Dictionary<string, HashSet<string>> carGoalCandidates;
            private readonly Dictionary<string, Dictionary<string,int>> allDists;

            public TimesaverSolver(Dictionary<string, List<string>> graph, Dictionary<string, HashSet<string>> carGoalCandidates)
            {
                this.graph = graph;
                this.carGoalCandidates = carGoalCandidates;
                allDists = graph.Keys.ToDictionary(k => k, k => BFS_Distances(k));
            }

            private Dictionary<string,int> BFS_Distances(string start)
            {
                var dist = graph.Keys.ToDictionary(k => k, _ => int.MaxValue);
                var q = new Queue<string>();
                dist[start] = 0;
                q.Enqueue(start);
                while (q.Count > 0)
                {
                    var u = q.Dequeue();
                    foreach (var v in graph[u])
                    {
                        if (dist[v] == int.MaxValue)
                        {
                            dist[v] = dist[u] + 1;
                            q.Enqueue(v);
                        }
                    }
                }
                return dist;
            }

            private int Heuristic(State s)
            {
                int est = 0;
                foreach (var car in s.Cars.Keys.Concat(s.Attached))
                {
                    string node = s.Cars.ContainsKey(car) ? s.Cars[car] : s.Loco;
                    var goals = carGoalCandidates.ContainsKey(car) ? carGoalCandidates[car] : new HashSet<string> { node };
                    int best = goals.Min(g => allDists[node].ContainsKey(g) ? allDists[node][g] : int.MaxValue);
                    if (best == int.MaxValue) best = 0;
                    est += best;
                }
                return est;
            }

            private string StateKey(State s)
            {
                var cars = string.Join(";", s.Cars.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
                return $"{s.Loco}|{string.Join(",", s.Attached)}|{cars}";
            }

            private bool IsGoal(State s)
            {
                if (s.Attached.Count > 0) return false;
                foreach (var kv in s.Cars)
                {
                    if (!carGoalCandidates.TryGetValue(kv.Key, out var goals) || !goals.Contains(kv.Value))
                        return false;
                }
                return true;
            }

            private IEnumerable<(string action, int cost, State next)> Neighbors(State s)
            {
                // MOVE to neighboring node (allow moving into a node that has a car so loco can couple)
                foreach (var nb in graph[s.Loco])
                {
                    yield return ($"MOVE {s.Loco}->{nb}", 1, new State(nb, s.Attached, new Dictionary<string, string>(s.Cars)));
                }

                // COUPLE: attach any cars that are at the loco's current node
                var chainAtLoco = s.Cars.Where(kv => kv.Value == s.Loco).Select(kv => kv.Key).ToList();
                if (chainAtLoco.Count > 0)
                {
                    var newCars = new Dictionary<string, string>(s.Cars);
                    foreach (var id in chainAtLoco) newCars.Remove(id);
                    var newAttached = s.Attached.Concat(chainAtLoco).ToList();
                    yield return ($"COUPLE at {s.Loco} -> [{string.Join(", ", chainAtLoco)}]", 1, new State(s.Loco, newAttached, newCars));
                }

                // DECOUPLE: drop attached cars at the loco's current node
                if (s.Attached.Count > 0)
                {
                    var newCars = new Dictionary<string, string>(s.Cars);
                    foreach (var id in s.Attached) newCars[id] = s.Loco;
                    yield return ($"DECOUPLE at {s.Loco} -> [{string.Join(", ", s.Attached)}]", 1, new State(s.Loco, new List<string>(), newCars));
                }
            }

            public List<ActionStep>? Solve(State start, int maxSteps = 1000000000)
            {
                var frontier = new PriorityQueue<(State state, int cost), int>();
                var startKey = StateKey(start);
                var cameFrom = new Dictionary<string, string?>();
                var actionFrom = new Dictionary<string, string?>();
                var costSoFar = new Dictionary<string, int> { [startKey] = 0 };

                frontier.Enqueue((start, 0), Heuristic(start));
                cameFrom[startKey] = null;
                actionFrom[startKey] = null;

                Console.WriteLine($"Start: loco={start.Loco}, h={Heuristic(start)}");

                int steps = 0;
                while (frontier.Count > 0 && steps < maxSteps)
                {
                    var (current, g) = frontier.Dequeue();
                    steps++;
                    var curKey = StateKey(current);

                    if (steps % 1000 == 0)
                    {
                        Console.WriteLine($"Step {steps:N0}, frontier {frontier.Count}, g={g}, h={Heuristic(current)}");
                    }

                    if (IsGoal(current))
                    {
                        var path = new List<(string key, string? action)>();
                        var k = curKey;
                        while (k != null)
                        {
                            path.Add((k, actionFrom[k]));
                            cameFrom.TryGetValue(k, out var parent);
                            k = parent;
                        }
                        path.Reverse();

                        var result = new List<ActionStep>();
                        for (int i = 1; i < path.Count; i++)
                        {
                            var (key, action) = path[i];
                            var state = ParseStateKey(key);
                            result.Add(new ActionStep(action ?? "", state));
                        }
                        return result;
                    }

                    foreach (var (action, stepCost, next) in Neighbors(current))
                    {
                        var nextKey = StateKey(next);
                        int newCost = costSoFar[curKey] + stepCost;
                        if (!costSoFar.ContainsKey(nextKey) || newCost < costSoFar[nextKey])
                        {
                            costSoFar[nextKey] = newCost;
                            int priority = newCost + Heuristic(next);
                            frontier.Enqueue((next, newCost), priority);
                            cameFrom[nextKey] = curKey;
                            actionFrom[nextKey] = action;
                        }
                    }
                }

                return null;
            }

            private State ParseStateKey(string key)
            {
                var parts = key.Split('|');
                var loco = parts[0];
                var attached = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
                var cars = new Dictionary<string,string>();
                if (parts.Length > 2)
                {
                    foreach (var kvp in parts[2].Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = kvp.Split(':');
                        if (kv.Length == 2) cars[kv[0]] = kv[1];
                    }
                }
                return new State(loco, attached, cars);
            }
        }

        private static Dictionary<string, List<string>> BuildTimesaverLayout(Dictionary<string,int> sidingLengths)
        {
            var graph = new Dictionary<string, List<string>>();
            var tailNodes = new Dictionary<string,string>();

            foreach (var kv in sidingLengths)
            {
                var nodes = Enumerable.Range(0, kv.Value).Select(i => $"{kv.Key}{i}").ToList();
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (!graph.ContainsKey(nodes[i])) graph[nodes[i]] = new List<string>();
                    if (i > 0) graph[nodes[i]].Add(nodes[i - 1]);
                    if (i < nodes.Count - 1) graph[nodes[i]].Add(nodes[i + 1]);
                }
                tailNodes[kv.Key] = nodes.Last();
            }

            graph["MAIN"] = new List<string>();
            foreach (var tail in tailNodes.Values)
            {
                graph["MAIN"].Add(tail);
                graph[tail].Add("MAIN");
            }

            graph["M1"] = new List<string> { "MAIN" };
            graph["M2"] = new List<string> { "MAIN" };
            graph["MAIN"].Add("M1");
            graph["MAIN"].Add("M2");

            return graph;
        }
    }
}