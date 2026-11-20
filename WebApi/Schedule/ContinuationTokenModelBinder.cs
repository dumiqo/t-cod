using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WebApi.Schedule;

public sealed class ContinuationTokenModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var result = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        var text = result.FirstValue;

        if (string.IsNullOrEmpty(text))
        {
            return Task.CompletedTask;
        }

        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, result);

        if (ContinuationToken.TryDecode(text, out var token))
        {
            bindingContext.Result = ModelBindingResult.Success(token);
        }
        else
        {
            bindingContext.ModelState.TryAddModelError(
                bindingContext.ModelName,
                bindingContext.ModelMetadata.ModelBindingMessageProvider.AttemptedValueIsInvalidAccessor(text, bindingContext.ModelName));
        }

        return Task.CompletedTask;
    }
}
