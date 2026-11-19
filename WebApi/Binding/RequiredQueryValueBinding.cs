using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WebApi.Binding;

internal static class RequiredQueryValueBinding
{
    public delegate bool Parser<T>(string? text, out T value);

    public static Task BindAsync<T>(ModelBindingContext bindingContext, Parser<T> parser)
    {
        var modelName = string.IsNullOrEmpty(bindingContext.ModelName)
            ? bindingContext.FieldName
            : bindingContext.ModelName;
        var result = bindingContext.ValueProvider.GetValue(modelName);
        var text = result.FirstValue;

        if (string.IsNullOrEmpty(text))
        {
            bindingContext.ModelState.TryAddModelError(modelName, $"The {modelName} field is required.");
            return Task.CompletedTask;
        }

        bindingContext.ModelState.SetModelValue(modelName, result);

        if (!parser(text, out var value))
        {
            var message = bindingContext.ModelMetadata.ModelBindingMessageProvider
                .AttemptedValueIsInvalidAccessor(text, modelName);
            bindingContext.ModelState.TryAddModelError(modelName, message);
            return Task.CompletedTask;
        }

        bindingContext.Result = ModelBindingResult.Success(value);
        return Task.CompletedTask;
    }
}
