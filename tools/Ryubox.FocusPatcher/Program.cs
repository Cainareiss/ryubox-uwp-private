using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("Usage: Ryubox.FocusPatcher <input-assembly> <output-assembly> [back-suppression-donor]");
    return 2;
}

string inputPath = Path.GetFullPath(args[0]);
string outputPath = Path.GetFullPath(args[1]);

using ModuleDefinition module = ModuleDefinition.ReadModule(inputPath, new ReaderParameters
{
    ReadWrite = false,
    InMemory = true,
    ReadingMode = ReadingMode.Immediate,
});

TypeDefinition mainPage = module.Types.Single(type => type.FullName == "Ryujinx.Uwp.App.MainPage");
MethodDefinition moveGameSelection = mainPage.Methods.Single(method =>
    method.Name == "MoveGameSelection" &&
    method.Parameters.Count == 1 &&
    method.Parameters[0].ParameterType.MetadataType == MetadataType.Int32);

if (!moveGameSelection.HasBody)
{
    throw new InvalidOperationException("MoveGameSelection has no IL body.");
}

bool focusAlreadyPatched = moveGameSelection.Body.Instructions.Any(instruction =>
    instruction.Operand is MethodReference method && method.Name == "ContainerFromIndex");

if (!focusAlreadyPatched)
{
    FieldDefinition gameListView = mainPage.Fields.Single(field => field.Name == "GameListView");
    VariableDefinition selectedIndex = moveGameSelection.Body.Variables.First(variable =>
        variable.VariableType.MetadataType == MetadataType.Int32);

    TypeReference gridViewItem = module.GetTypeReferences().First(type =>
        type.FullName == "Windows.UI.Xaml.Controls.GridViewItem");
    TypeReference itemsControl = module.GetTypeReferences().First(type =>
        type.FullName == "Windows.UI.Xaml.Controls.ItemsControl");
    TypeReference dependencyObject = module.GetTypeReferences().First(type =>
        type.FullName == "Windows.UI.Xaml.DependencyObject");

    MethodReference focus = module.Types
        .SelectMany(type => type.Methods)
        .Where(method => method.HasBody)
        .SelectMany(method => method.Body.Instructions)
        .Select(instruction => instruction.Operand)
        .OfType<MethodReference>()
        .First(method =>
            method.Name == "Focus" &&
            method.DeclaringType.FullName == "Windows.UI.Xaml.Controls.Control" &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == "Windows.UI.Xaml.FocusState");

    MethodReference containerFromIndex = new("ContainerFromIndex", dependencyObject, itemsControl)
    {
        HasThis = true,
        CallingConvention = MethodCallingConvention.Default,
    };
    containerFromIndex.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));

    VariableDefinition selectedContainer = new(gridViewItem);
    moveGameSelection.Body.Variables.Add(selectedContainer);
    moveGameSelection.Body.InitLocals = true;

    ILProcessor il = moveGameSelection.Body.GetILProcessor();
    Instruction finalReturn = moveGameSelection.Body.Instructions.Last(instruction => instruction.OpCode == OpCodes.Ret);

    Instruction[] injected =
    [
        il.Create(OpCodes.Ldarg_0),
        il.Create(OpCodes.Ldfld, gameListView),
        il.Create(OpCodes.Ldloc, selectedIndex),
        il.Create(OpCodes.Callvirt, containerFromIndex),
        il.Create(OpCodes.Isinst, gridViewItem),
        il.Create(OpCodes.Stloc, selectedContainer),
        il.Create(OpCodes.Ldloc, selectedContainer),
        il.Create(OpCodes.Brfalse, finalReturn),
        il.Create(OpCodes.Ldloc, selectedContainer),
        il.Create(OpCodes.Ldc_I4_3),
        il.Create(OpCodes.Callvirt, focus),
        il.Create(OpCodes.Pop),
    ];

    foreach (Instruction instruction in injected)
    {
        il.InsertBefore(finalReturn, instruction);
    }
}

bool backSuppressionPatched = false;
if (args.Length == 3)
{
    string donorPath = Path.GetFullPath(args[2]);
    using ModuleDefinition donor = ModuleDefinition.ReadModule(donorPath, new ReaderParameters
    {
        ReadWrite = false,
        InMemory = true,
        ReadingMode = ReadingMode.Immediate,
    });

    TypeDefinition targetApp = module.Types.Single(type => type.FullName == "Ryujinx.Uwp.App.App");
    TypeDefinition donorApp = donor.Types.Single(type => type.FullName == "Ryujinx.Uwp.App.App");
    if (targetApp.Methods.Any(method => method.Name == "InstallSystemBackSuppression"))
    {
        throw new InvalidOperationException("System back suppression is already patched.");
    }

    FieldDefinition donorField = donorApp.Fields.Single(field => field.Name == "_systemBackSuppressionInstalled");
    FieldDefinition targetField = new(
        donorField.Name,
        donorField.Attributes,
        module.ImportReference(donorField.FieldType));
    targetApp.Fields.Add(targetField);

    MethodDefinition donorHandler = donorApp.Methods.Single(method => method.Name == "OnSystemBackRequested");
    MethodDefinition donorInstaller = donorApp.Methods.Single(method => method.Name == "InstallSystemBackSuppression");
    MethodDefinition targetHandler = CloneMethodSkeleton(module, donorHandler);
    MethodDefinition targetInstaller = CloneMethodSkeleton(module, donorInstaller);
    targetApp.Methods.Add(targetHandler);
    targetApp.Methods.Add(targetInstaller);

    Dictionary<string, MethodDefinition> internalMethods = new(StringComparer.Ordinal)
    {
        [donorHandler.FullName] = targetHandler,
        [donorInstaller.FullName] = targetInstaller,
    };
    Dictionary<string, FieldDefinition> internalFields = new(StringComparer.Ordinal)
    {
        [donorField.FullName] = targetField,
    };

    CloneMethodBody(module, donorHandler, targetHandler, internalMethods, internalFields);
    CloneMethodBody(module, donorInstaller, targetInstaller, internalMethods, internalFields);

    MethodDefinition onLaunched = targetApp.Methods.Single(method => method.Name == "OnLaunched");
    ILProcessor launchedIl = onLaunched.Body.GetILProcessor();
    Instruction first = onLaunched.Body.Instructions[0];
    launchedIl.InsertBefore(first, launchedIl.Create(OpCodes.Ldarg_0));
    launchedIl.InsertBefore(first, launchedIl.Create(OpCodes.Call, targetInstaller));
    backSuppressionPatched = true;
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
module.Write(outputPath, new WriterParameters());

Console.WriteLine($"Patched: {inputPath}");
Console.WriteLine($"Output:  {outputPath}");
Console.WriteLine(focusAlreadyPatched
    ? "Change: controller focus patch was already present."
    : "Change: MoveGameSelection now focuses the selected GridViewItem.");
Console.WriteLine(backSuppressionPatched
    ? "Change: Xbox/UWP system Back is consumed without blocking the raw gamepad B input."
    : "Change: no system Back donor was supplied.");
return 0;

static MethodDefinition CloneMethodSkeleton(ModuleDefinition targetModule, MethodDefinition source)
{
    MethodDefinition clone = new(
        source.Name,
        source.Attributes,
        targetModule.ImportReference(source.ReturnType))
    {
        ImplAttributes = source.ImplAttributes,
    };
    foreach (ParameterDefinition parameter in source.Parameters)
    {
        clone.Parameters.Add(new ParameterDefinition(
            parameter.Name,
            parameter.Attributes,
            targetModule.ImportReference(parameter.ParameterType)));
    }
    return clone;
}

static void CloneMethodBody(
    ModuleDefinition targetModule,
    MethodDefinition source,
    MethodDefinition target,
    IReadOnlyDictionary<string, MethodDefinition> internalMethods,
    IReadOnlyDictionary<string, FieldDefinition> internalFields)
{
    if (!source.HasBody || source.Body.ExceptionHandlers.Count != 0)
    {
        throw new InvalidOperationException($"Unsupported donor method body: {source.FullName}");
    }

    target.Body = new MethodBody(target)
    {
        InitLocals = source.Body.InitLocals,
        MaxStackSize = source.Body.MaxStackSize,
    };
    Dictionary<VariableDefinition, VariableDefinition> variables = new();
    foreach (VariableDefinition variable in source.Body.Variables)
    {
        VariableDefinition clone = new(targetModule.ImportReference(variable.VariableType));
        target.Body.Variables.Add(clone);
        variables[variable] = clone;
    }

    Dictionary<Instruction, Instruction> instructions = new();
    foreach (Instruction instruction in source.Body.Instructions)
    {
        Instruction clone = Instruction.Create(OpCodes.Nop);
        clone.OpCode = instruction.OpCode;
        target.Body.Instructions.Add(clone);
        instructions[instruction] = clone;
    }

    foreach (Instruction instruction in source.Body.Instructions)
    {
        Instruction clone = instructions[instruction];
        clone.Operand = instruction.Operand switch
        {
            null => null,
            Instruction branch => instructions[branch],
            Instruction[] branches => branches.Select(branch => instructions[branch]).ToArray(),
            VariableDefinition variable => variables[variable],
            ParameterDefinition parameter => target.Parameters[parameter.Index],
            MethodReference method when internalMethods.TryGetValue(method.FullName, out MethodDefinition? mapped) => mapped,
            MethodReference method when method.DeclaringType.FullName == "Ryujinx.Uwp.App.UwpFrontendLog" =>
                targetModule.Types.Single(type => type.FullName == method.DeclaringType.FullName)
                    .Methods.Single(candidate => candidate.Name == method.Name && candidate.Parameters.Count == method.Parameters.Count),
            MethodReference method => targetModule.ImportReference(method),
            FieldReference field when internalFields.TryGetValue(field.FullName, out FieldDefinition? mapped) => mapped,
            FieldReference field => targetModule.ImportReference(field),
            TypeReference type => targetModule.ImportReference(type),
            _ => instruction.Operand,
        };
    }
}
