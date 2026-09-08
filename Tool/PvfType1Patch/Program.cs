using PvfLib;

if (args.Length < 4)
{
    Console.Error.WriteLine(
        "Usage: PvfType1Patch <input.pvf> <output.pvf> <mob-path> <name>=<int> [<name>=<int> ...]");
    Console.Error.WriteLine(
        "Example: PvfType1Patch Script.pvf Script.out.pvf monster/Tau/TauGuard.mob warlike=80");
    Console.Error.WriteLine(
        "Example: PvfType1Patch Script.pvf Script.out.pvf monster/Tau/TauGuard.mob \"HP MAX\"=300 EQUIPMENT_PHYSICAL_ATTACK=1000");
    return 1;
}

var inputPath = args[0];
var outputPath = args[1];
var mobPath = args[2];
if (!File.Exists(inputPath))
{
    Console.Error.WriteLine("input PVF not found: " + inputPath);
    return 1;
}

using var archive = PvfArchive.Open(inputPath);
for (var i = 3; i < args.Length; i++)
{
    var parts = args[i].Split('=', 2);
    if (parts.Length != 2 || !int.TryParse(parts[1], out var value))
    {
        Console.Error.WriteLine("expected name=integer: " + args[i]);
        return 1;
    }

    if (!PvfType1AbilityPatch.TryReplaceValue(archive, mobPath, parts[0], value, out var error))
    {
        Console.Error.WriteLine(error ?? "patch failed");
        return 1;
    }

    Console.WriteLine("patched " + mobPath + " " + parts[0] + "=" + value);
}

archive.SaveAs(outputPath);
Console.WriteLine("wrote " + outputPath);
return 0;
