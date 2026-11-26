using System.CommandLine;
using System.Reflection;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Client;

public static partial class DmsCommandApp
{
    private static IEnumerable<ActionCommand> DeclaredMachineCommands(string group)
    {
        var factory = typeof(DmsCommandApp).GetMethod(nameof(BindDeclaredCommand), BindingFlags.NonPublic | BindingFlags.Static)!;
        // Only shared operation declarations opt in. A CLI attribute cannot make an internal workflow public.
        foreach (var field in typeof(OperationsWorkflows).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetCustomAttribute<WorkflowCommandAttribute>() is not { } command) continue;
            if (!field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(MachineWorkflowDefinition<,>) ||
                field.GetValue(null) is not WorkflowDefinition { PublicEntry: true } definition)
                throw new InvalidOperationException($"{field.Name} must declare a public machine workflow to expose a command.");

            var separator = definition.Name.LastIndexOf('.');
            if (separator < 0 || definition.Name[..separator] != group) continue;
            var name = definition.Name[(separator + 1)..];
            yield return (ActionCommand)factory.MakeGenericMethod(field.FieldType.GenericTypeArguments)
                .Invoke(null, BindingFlags.DoNotWrapExceptions, null, [name, command.Description, definition], null)!;
        }
    }

    private static ActionCommand BindDeclaredCommand<TInput, TResult>(string name, string description,
        MachineWorkflowDefinition<TInput, TResult> definition)
    {
        if (typeof(TInput).GetConstructors() is not [var constructor])
            throw new InvalidOperationException($"{definition.Name} needs a single public input constructor for CLI binding.");

        var options = constructor.GetParameters().Select(parameter =>
        {
            if (parameter.ParameterType != typeof(string) || parameter.HasDefaultValue ||
                parameter.GetCustomAttribute<WorkflowOptionAttribute>() is not { } option)
                throw new InvalidOperationException($"{definition.Name} requires an explicitly declared string option for {parameter.Name}.");
            return new Option<string>(option.Name) { Required = true, Description = option.Description };
        }).ToArray();

        return StandardCommand(name, description, definition,
            parsed => (TInput)constructor.Invoke(BindingFlags.DoNotWrapExceptions, null,
                [.. options.Select(option => parsed.GetValue(option))], null), [.. options]);
    }
}
