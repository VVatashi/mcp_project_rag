namespace ProjectRag.Code.Tests;

/// <summary>
/// The sources of the small code base that the tests extract.
/// </summary>
internal static class SampleCodeBase
{
    public const string LibProject = "src/Lib/Lib.csproj";

    public const string AppProject = "src/App/App.csproj";

    public const string Solution = "Sample.slnx";

    public static IReadOnlyDictionary<string, string> Files { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Solution] = """
            <Solution>
              <Project Path="src/Lib/Lib.csproj" />
              <Project Path="src/App/App.csproj" />
            </Solution>
            """,
        [LibProject] = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
              </PropertyGroup>
              <PropertyGroup Condition="'$(SampleFlag)' == 'true'">
                <DefineConstants>$(DefineConstants);SAMPLE_FLAG</DefineConstants>
              </PropertyGroup>
            </Project>
            """,
        [AppProject] = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
                <Compile Include="..\Lib\Shared.cs" Link="Shared.cs" />
              </ItemGroup>
            </Project>
            """,
        ["src/Lib/IShape.cs"] = """
            namespace Lib;

            /// <summary>A shape with an area.</summary>
            public interface IShape
            {
                double Area();
            }
            """,
        ["src/Lib/ShapeBase.cs"] = """
            namespace Lib;

            public abstract class ShapeBase : IShape
            {
                public abstract double Area();
            }
            """,
        ["src/Lib/Marker.cs"] = """
            namespace Lib;

            [System.AttributeUsage(System.AttributeTargets.All)]
            public sealed class MarkerAttribute : System.Attribute
            {
            }
            """,
        ["src/Lib/Circle.cs"] = """
            using System;

            namespace Lib;

            /// <summary>A circle with a radius.</summary>
            [Marker]
            public sealed class Circle : ShapeBase
            {
                public const int Sides = 0;

                public Circle()
                {
                }

                public Circle(double radius)
                    : this()
                {
                    Radius = radius;
                }

                public double Radius { get; }

                public event EventHandler? Changed;

                public static Circle operator +(Circle left, Circle right) => new Circle(left.Radius + right.Radius);

                public override double Area() => Math.PI * Radius * Radius;
            }
            """,
        ["src/Lib/Scene.cs"] = """
            using System.Collections.Generic;

            namespace Lib;

            /// <summary>
            /// Holds the <see cref="Circle"/> items,
            /// or <see langword="null"/>.   Use <c>Make</c>.
            /// </summary>
            public partial class Scene
            {
                private readonly Circle _default = new Circle();

                private readonly List<Circle> _items = new();

                private readonly Counter _counter = new Counter();

                public Circle Make(double radius)
                {
                    Circle circle = new Circle(radius);
                    _items.Add(circle);
                    _counter.Increment();
                    return circle;
                }
            }
            """,
        ["src/Lib/Scene.Extra.cs"] = """
            namespace Lib;

            public partial class Scene
            {
                public int Count => _items.Count;

                public Registry<Circle> Registry { get; } = new();

                public Circle this[int index] => _items[index];

                public double Total()
                {
                    double total = 0;
                    foreach (Circle item in _items)
                        total += item.Area();

                    return total;
                }
            }
            """,
        ["src/Lib/Registry.cs"] = """
            using System.Collections.Generic;

            namespace Lib;

            public class Registry<T>
            {
                private readonly List<T> _items = new List<T>();

                public void Add(T item) => _items.Add(item);
            }
            """,
        ["src/Lib/Kinds.cs"] = """
            namespace Lib;

            public enum Color
            {
                Red,
                Green,
            }

            public record Tag
            {
                public string Name { get; init; } = string.Empty;
            }

            public readonly record struct Id(int Value);

            public struct Pair
            {
                public int A;

                public int B;

                public static implicit operator int(Pair pair) => pair.A + pair.B;
            }

            public delegate void ShapeHandler(IShape shape);
            """,
        ["src/Lib/Extensions.cs"] = """
            namespace Lib;

            public static class ShapeExtensions
            {
                public static double Doubled(this IShape shape) => shape.Area() * 2;
            }
            """,
        ["src/Lib/Conditional.cs"] = """
            namespace Lib;

            public sealed class Flagged
            {
            #if SAMPLE_FLAG
                public Circle OnlyWithFlag() => new Circle();
            #endif
            }
            """,
        ["src/Lib/Shared.cs"] = """
            namespace Lib;

            public class Counter
            {
                static Counter()
                {
                }

                ~Counter()
                {
                }

                public int Value { get; private set; }

                public void Increment() => Value++;
            }
            """,
        ["src/App/Consumer.cs"] = """
            using Lib;

            namespace App;

            public sealed class Consumer
            {
                private readonly Counter _counter = new Counter();

                public double Run()
                {
                    Circle circle = new Circle(1.5);
                    _counter.Increment();
                    return circle.Doubled() + circle.Area();
                }
            }
            """,
    };
}
