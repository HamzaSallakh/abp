using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Reflection;

namespace Volo.Abp.AspNetCore.Mvc.ApiExploring;

public class AbpRemoteServiceApiDescriptionProvider : IApiDescriptionProvider, ITransientDependency
{
    private readonly IModelMetadataProvider _modelMetadataProvider;
    private readonly MvcOptions _mvcOptions;
    private readonly AbpRemoteServiceApiDescriptionProviderOptions _options;

    // SupportedResponseTypes lives on a singleton options object, so the build below must run
    // exactly once and single-threaded. Otherwise concurrent API-description rebuilds mutate the
    // same ApiResponseFormats List<> at the same time and corrupt it ("Source array was not long
    // enough"), and repeated rebuilds append duplicates without bound.
    private static readonly object SyncLock = new object();
    private static volatile bool _responseTypesInitialized;

    public AbpRemoteServiceApiDescriptionProvider(
        IModelMetadataProvider modelMetadataProvider,
        IOptions<MvcOptions> mvcOptionsAccessor,
        IOptions<AbpRemoteServiceApiDescriptionProviderOptions> optionsAccessor)
    {
        _modelMetadataProvider = modelMetadataProvider;
        _mvcOptions = mvcOptionsAccessor.Value;
        _options = optionsAccessor.Value;
    }

    public void OnProvidersExecuted(ApiDescriptionProviderContext context)
    {
    }

    /// <summary>
    /// The order -999 ensures that this provider is executed right after the
    /// Microsoft.AspNetCore.Mvc.ApiExplorer.DefaultApiDescriptionProvider.
    /// </summary>
    public int Order => -999;

    public void OnProvidersExecuting(ApiDescriptionProviderContext context)
    {
        foreach (var apiResponseType in GetApiResponseTypes())
        {
            foreach (var result in context.Results.Where(x => x.IsRemoteService()))
            {
                var actionProducesResponseTypeAttributes =
                    ReflectionHelper.GetAttributesOfMemberOrDeclaringType<ProducesResponseTypeAttribute>(
                        result.ActionDescriptor.GetMethodInfo());
                if (actionProducesResponseTypeAttributes.Any(x => x.StatusCode == apiResponseType.StatusCode))
                {
                    continue;
                }

                result.SupportedResponseTypes.AddIfNotContains(x => x.StatusCode == apiResponseType.StatusCode,
                    () => apiResponseType);
            }
        }
    }

    protected virtual IEnumerable<ApiResponseType> GetApiResponseTypes()
    {
        // Fast path: already built once. Return the shared (now read-only) collection.
        if (_responseTypesInitialized)
        {
            return _options.SupportedResponseTypes;
        }

        lock (SyncLock)
        {
            if (_responseTypesInitialized)
            {
                return _options.SupportedResponseTypes;
            }

            foreach (var apiResponse in _options.SupportedResponseTypes)
            {
                apiResponse.ModelMetadata = _modelMetadataProvider.GetMetadataForType(apiResponse.Type!);

                // Clear first so repeated builds don't accumulate duplicate formats on the shared list.
                apiResponse.ApiResponseFormats.Clear();

                foreach (var responseTypeMetadataProvider in _mvcOptions.OutputFormatters.OfType<IApiResponseTypeMetadataProvider>())
                {
                    var formatterSupportedContentTypes = responseTypeMetadataProvider.GetSupportedContentTypes(null!, apiResponse.Type!);
                    if (formatterSupportedContentTypes == null)
                    {
                        continue;
                    }

                    foreach (var formatterSupportedContentType in formatterSupportedContentTypes)
                    {
                        apiResponse.ApiResponseFormats.Add(new ApiResponseFormat
                        {
                            Formatter = (IOutputFormatter)responseTypeMetadataProvider,
                            MediaType = formatterSupportedContentType
                        });
                    }
                }
            }

            _responseTypesInitialized = true;
            return _options.SupportedResponseTypes;
        }
    }
}
