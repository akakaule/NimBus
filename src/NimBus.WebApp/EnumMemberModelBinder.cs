using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace NimBus.WebApp;

public class EnumMemberModelBinderProvider : IModelBinderProvider
{
    public IModelBinder GetBinder(ModelBinderProviderContext context)
    {
        if (context.Metadata.ModelType.IsEnum &&
            context.Metadata.ModelType.GetFields().Any(f => f.GetCustomAttribute<EnumMemberAttribute>() != null))
        {
            return new EnumMemberModelBinder(context.Metadata.ModelType);
        }
        return null;
    }
}

public class EnumMemberModelBinder : IModelBinder
{
    private readonly Type _enumType;

    public EnumMemberModelBinder(Type enumType) => _enumType = enumType;

    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var value = bindingContext.ValueProvider.GetValue(bindingContext.ModelName).FirstValue;
        if (string.IsNullOrEmpty(value))
            return Task.CompletedTask;

        foreach (var field in _enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attr = field.GetCustomAttribute<EnumMemberAttribute>();
            if (attr?.Value == value || field.Name == value)
            {
                bindingContext.Result = ModelBindingResult.Success(field.GetValue(null));
                return Task.CompletedTask;
            }
        }

        if (Enum.TryParse(_enumType, value, ignoreCase: true, out var parsed))
        {
            bindingContext.Result = ModelBindingResult.Success(parsed);
            return Task.CompletedTask;
        }

        bindingContext.ModelState.AddModelError(bindingContext.ModelName, $"Invalid value '{value}' for {_enumType.Name}.");
        return Task.CompletedTask;
    }
}
