using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WebApi.Binding;

public sealed class IsoDateModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        return RequiredQueryValueBinding.BindAsync<DateOnly>(bindingContext, IsoDateParser.TryParse);
    }
}
