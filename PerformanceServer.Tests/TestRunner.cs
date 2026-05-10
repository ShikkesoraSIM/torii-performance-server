// Copyright (c) 2025 GooGuTeam
// Licensed under the MIT Licence. See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Internal;
using NUnit.Framework.Internal.Builders;

namespace PerformanceServer.Tests
{
    /// <summary>
    /// In-process NUnit runner so the test suite can be executed without
    /// the VS testhost process spawning. The default `dotnet test` flow
    /// uses a side-car host that's blocked in a few CI / sandboxed
    /// environments — this entry point sidesteps that by walking each
    /// [Test] method via reflection and reporting pass/fail directly.
    /// Run with: `dotnet run --project PerformanceServer.Tests`.
    /// </summary>
    public static class TestRunner
    {
        public static int Main()
        {
            int passed = 0, failed = 0;
            var assembly = typeof(TouchScreenClassifierTests).Assembly;

            // Discover [TestFixture]-annotated types in this assembly.
            foreach (var type in assembly.GetTypes()
                                         .Where(t => t.GetCustomAttribute<TestFixtureAttribute>() != null))
            {
                Console.WriteLine($"\n=== {type.Name} ===");
                object? instance = Activator.CreateInstance(type);
                if (instance == null)
                {
                    Console.WriteLine($"  [SKIP] could not construct {type.Name}");
                    continue;
                }

                // Each [Test]-annotated method runs in isolation; failures
                // surface as exceptions thrown by NUnit's assert helpers.
                foreach (var method in type.GetMethods()
                                           .Where(m => m.GetCustomAttribute<TestAttribute>() != null))
                {
                    try
                    {
                        method.Invoke(instance, null);
                        Console.WriteLine($"  [PASS] {method.Name}");
                        passed++;
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException != null)
                    {
                        // NUnit's Assert.Pass() / Assert.Inconclusive() and a
                        // few related helpers short-circuit by throwing a
                        // ResultStateException (SuccessException /
                        // InconclusiveException). The reflection layer wraps
                        // those in TargetInvocationException; treat anything
                        // whose inner-type name starts with "Success" as a
                        // pass, otherwise it's a real failure.
                        var inner = ex.InnerException;
                        var innerTypeName = inner.GetType().Name;
                        if (innerTypeName == "SuccessException")
                        {
                            Console.WriteLine($"  [PASS] {method.Name}");
                            passed++;
                        }
                        else
                        {
                            Console.WriteLine($"  [FAIL] {method.Name}");
                            Console.WriteLine($"         {inner.Message}");
                            failed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  [FAIL] {method.Name}: {ex.Message}");
                        failed++;
                    }
                }
            }

            Console.WriteLine($"\n────────────────────────────────");
            Console.WriteLine($"  {passed} passed, {failed} failed");
            Console.WriteLine($"────────────────────────────────");
            return failed == 0 ? 0 : 1;
        }
    }
}
