using System.Collections;
using System.Runtime.InteropServices;

namespace BrightnessTray.Windows;

// Late-bound COM avoids a System.Management package and keeps all RCWs local to each operation.
internal sealed class WmiSession : IDisposable
{
    private readonly object _locator;
    private readonly object _services;

    internal WmiSession()
    {
        var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!;
        _locator = Activator.CreateInstance(locatorType)!;
        object? services = null;
        try
        {
            services = ((dynamic)_locator).ConnectServer(".", @"root\wmi", null, null, null, null, 128);
            object security = ((dynamic)services).Security_;
            try { ((dynamic)security).ImpersonationLevel = 3; }
            finally { Release(security); }
            _services = services;
        }
        catch
        {
            Release(services);
            Release(_locator);
            throw;
        }
    }

    internal void Query(string query, Action<object> visit)
    {
        // Untyped .NET COM enumeration can reopen _NewEnum; avoid forward-only WMI result sets.
        object results = ((dynamic)_services).ExecQuery(query, "WQL", 16);
        IEnumerator? iterator = null;
        try
        {
            iterator = ((IEnumerable)results).GetEnumerator();
            while (iterator.MoveNext())
            {
                object item = iterator.Current!;
                try { visit(item); }
                finally { Release(item); }
            }
        }
        finally
        {
            if (iterator is IDisposable disposable) disposable.Dispose();
            Release(iterator);
            Release(results);
        }
    }

    internal static object? ReadValue(object item, string name)
    {
        object? properties = null;
        object? property = null;
        try
        {
            properties = ((dynamic)item).Properties_;
            if (properties is null)
                throw new InvalidOperationException("The driver did not expose WMI properties for this display.");
            property = ((dynamic)properties).Item(name);
            if (property is null)
                throw new InvalidOperationException($"The driver did not expose the WMI property '{name}'.");
            return ((dynamic)property).Value;
        }
        finally
        {
            Release(property);
            Release(properties);
        }
    }

    internal void ValidateBrightnessControl()
    {
        // Preparing an input object only inspects the schema; it does not invoke the driver.
        var input = CreateBrightnessInput(0, out _);
        Release(input);
    }

    internal void SetBrightness(object item, byte brightness)
    {
        object? input = null;
        object? output = null;
        try
        {
            input = CreateBrightnessInput(brightness, out var declaresOutput);
            output = ((dynamic)item).ExecMethod_("WmiSetBrightness", input, 0);
            WmiBrightnessResult.Validate(declaresOutput, output, ReadValue);
        }
        finally
        {
            Release(output);
            Release(input);
        }
    }

    private object CreateBrightnessInput(byte brightness, out bool declaresOutput)
    {
        object? definition = null;
        object? methods = null;
        object? method = null;
        object? inputTemplate = null;
        object? outputTemplate = null;
        object? input = null;
        try
        {
            // Query instances may omit method signatures; obtain the schema from the class.
            definition = ((dynamic)_services).Get("WmiMonitorBrightnessMethods", 0);
            if (definition is null) throw BrightnessControlUnavailable();
            methods = ((dynamic)definition).Methods_;
            if (methods is null) throw BrightnessControlUnavailable();
            method = ((dynamic)methods).Item("WmiSetBrightness");
            if (method is null) throw BrightnessControlUnavailable();
            outputTemplate = ((dynamic)method).OutParameters;
            declaresOutput = outputTemplate is not null;
            inputTemplate = ((dynamic)method).InParameters;
            if (inputTemplate is null) throw BrightnessControlUnavailable();
            input = ((dynamic)inputTemplate).SpawnInstance_();
            if (input is null) throw BrightnessControlUnavailable();
            WriteValue(input, "Timeout", 0u);
            WriteValue(input, "Brightness", brightness);
            var prepared = input;
            input = null; // The caller owns the prepared input object.
            return prepared;
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException("The built-in display brightness controls are unavailable. Turn on the display, then refresh the display list.", exception);
        }
        finally
        {
            Release(input);
            Release(inputTemplate);
            Release(outputTemplate);
            Release(method);
            Release(methods);
            Release(definition);
        }
    }

    private static InvalidOperationException BrightnessControlUnavailable() =>
        new("The built-in display brightness controls are unavailable. Turn on the display, then refresh the display list.");

    private static void WriteValue(object item, string name, object value)
    {
        object? properties = null;
        object? property = null;
        try
        {
            properties = ((dynamic)item).Properties_;
            if (properties is null) throw BrightnessControlUnavailable();
            property = ((dynamic)properties).Item(name);
            if (property is null) throw BrightnessControlUnavailable();
            ((dynamic)property).Value = value;
        }
        finally
        {
            Release(property);
            Release(properties);
        }
    }

    public void Dispose()
    {
        Release(_services);
        Release(_locator);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
