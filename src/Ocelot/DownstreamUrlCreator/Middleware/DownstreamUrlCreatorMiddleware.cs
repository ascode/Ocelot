using System.Threading.Tasks;
using Ocelot.DownstreamUrlCreator.UrlTemplateReplacer;
using Ocelot.Logging;
using Ocelot.Middleware;
using System;
using Ocelot.Responses;
using Ocelot.Values;

namespace Ocelot.DownstreamUrlCreator.Middleware
{
    using System.Text.RegularExpressions;

    public class DownstreamUrlCreatorMiddleware : OcelotMiddleware
    {
        private readonly OcelotRequestDelegate _next;
        private readonly IDownstreamPathPlaceholderReplacer _replacer;

        public DownstreamUrlCreatorMiddleware(OcelotRequestDelegate next,
            IOcelotLoggerFactory loggerFactory,
            IDownstreamPathPlaceholderReplacer replacer)
                :base(loggerFactory.CreateLogger<DownstreamUrlCreatorMiddleware>())
        {
            _next = next;
            _replacer = replacer;
        }

        public async Task Invoke(DownstreamContext context)
        {
            var response = _replacer
                .Replace(context.DownstreamReRoute.DownstreamPathTemplate, context.TemplatePlaceholderNameAndValues);

            if (response.IsError)
            {
                Logger.LogDebug("IDownstreamPathPlaceholderReplacer returned an error, setting pipeline error");

                SetPipelineError(context, response.Errors);
                return;
            }

            context.DownstreamRequest.Scheme = context.DownstreamReRoute.DownstreamScheme;

            if (ServiceFabricRequest(context))
            {
                var pathAndQuery = CreateServiceFabricUri(context, response);
                context.DownstreamRequest.AbsolutePath = pathAndQuery.path;
                context.DownstreamRequest.Query = pathAndQuery.query;
            }
            else
            {
                var dsPath = response.Data;

                if(ContainsQueryString(dsPath))
                {
                    context.DownstreamRequest.AbsolutePath = GetPath(dsPath);
                    context.DownstreamRequest.Query = GetQueryString(dsPath);
                }
                else
                {
                    RemoveQueryStringParametersThatHaveBeenUsedInTemplate(context);

                    context.DownstreamRequest.AbsolutePath = dsPath.Value;
                }
            }

            Logger.LogDebug($"Downstream url is {context.DownstreamRequest}");

            await _next.Invoke(context);
        }

        private static void RemoveQueryStringParametersThatHaveBeenUsedInTemplate(DownstreamContext context)
        {
            foreach (var nAndV in context.TemplatePlaceholderNameAndValues)
            {
                var name = nAndV.Name.Replace("{", "").Replace("}", "");

                if (context.DownstreamRequest.Query.Contains(name) &&
                    context.DownstreamRequest.Query.Contains(nAndV.Value))
                {
                    var questionMarkOrAmpersand = context.DownstreamRequest.Query.IndexOf(name, StringComparison.Ordinal);
                    context.DownstreamRequest.Query = context.DownstreamRequest.Query.Remove(questionMarkOrAmpersand - 1, 1);

                    var rgx = new Regex($@"\b{name}={nAndV.Value}\b");
                    context.DownstreamRequest.Query = rgx.Replace(context.DownstreamRequest.Query, "");

                    if (!string.IsNullOrEmpty(context.DownstreamRequest.Query))
                    {
                        context.DownstreamRequest.Query = '?' + context.DownstreamRequest.Query.Substring(1);
                    }
                }
            }
        }

        private string GetPath(DownstreamPath dsPath)
        {
            return dsPath.Value.Substring(0, dsPath.Value.IndexOf("?", StringComparison.Ordinal));
        }

         private string GetQueryString(DownstreamPath dsPath)
        {
            return dsPath.Value.Substring(dsPath.Value.IndexOf("?", StringComparison.Ordinal));
        }

        private bool ContainsQueryString(DownstreamPath dsPath)
        {
            return dsPath.Value.Contains("?");
        }

        private (string path, string query) CreateServiceFabricUri(DownstreamContext context, Response<DownstreamPath> dsPath)
        {
            var query = context.DownstreamRequest.Query;           
            var serviceFabricPath = $"/{context.DownstreamReRoute.ServiceName + dsPath.Data.Value}";

            if (RequestForStatefullService(query))
            {
                return (serviceFabricPath, query);
            }

            var split = string.IsNullOrEmpty(query) ? "?" : "&";
            return (serviceFabricPath, $"{query}{split}cmd=instance");
        }

        private static bool ServiceFabricRequest(DownstreamContext context)
        {
            return context.Configuration.ServiceProviderConfiguration.Type == "ServiceFabric" && context.DownstreamReRoute.UseServiceDiscovery;
        }

        private static bool RequestForStatefullService(string query)
        {
            return query.Contains("PartitionKind") && query.Contains("PartitionKey");
        }
    }
}
