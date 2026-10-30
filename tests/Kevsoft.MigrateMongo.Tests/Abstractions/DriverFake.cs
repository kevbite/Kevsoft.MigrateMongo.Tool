using System.Reflection;

namespace Kevsoft.MigrateMongo.Tests.Abstractions;

/// <summary>A driver interface whose members are answered by a function, to test the tracked wrappers without a server.</summary>
public class DriverFake : DispatchProxy
{
    private Func<MethodInfo, object?[], object?> _answer = (_, _) => null;

    public static T Of<T>(Func<MethodInfo, object?> answer)
        where T : class => Of<T>((method, _) => answer(method));

    /// <summary>A fake answered from the member called and its arguments.</summary>
    public static T Of<T>(Func<MethodInfo, object?[], object?> answer)
        where T : class
    {
        var fake = Create<T, DriverFake>();
        ((DriverFake)(object)fake)._answer = answer;
        return fake;
    }

    /// <summary>A faulted <see cref="Task"/> or <see cref="Task{TResult}"/> for a method returning <paramref name="returnType"/>.</summary>
    public static object Faulted(Type returnType, Exception exception) =>
        returnType == typeof(Task)
            ? Task.FromException(exception)
            : typeof(Task).GetMethods()
                .Single(m => m is { Name: nameof(Task.FromException), IsGenericMethodDefinition: true })
                .MakeGenericMethod(returnType.GetGenericArguments()[0])
                .Invoke(null, [exception])!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _answer(targetMethod!, args ?? []);
}
