using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WebApi.Binding;

public sealed class UtcDateTimeModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        return RequiredQueryValueBinding.BindAsync<DateTime>(bindingContext, UtcDateTimeParser.TryParse);
    }
}
