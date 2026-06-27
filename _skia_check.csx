using System;
using System.Linq;
using System.Reflection;

var asm = Assembly.LoadFrom(@"G:\ai\steamingitems\Steaming.WinUI\bin\Debug\net10.0-windows10.0.19041.0\win-x64\SkiaSharp.dll");
var t = asm.GetType("SkiaSharp.SKPixmap");
Console.WriteLine("SKPixmap methods:");
foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance).OrderBy(x => x.Name))
    Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");

Console.WriteLine("\nSKSurface methods containing 'Snapshot' or 'Pixel':");
var s = asm.GetType("SkiaSharp.SKSurface");
foreach (var m in s.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name.Contains("Snap") || m.Name.Contains("Pixel")).OrderBy(x => x.Name))
    Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
