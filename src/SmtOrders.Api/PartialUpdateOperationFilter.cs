using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace SmtOrders.Api;

public sealed class PartialUpdateOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var requestType = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<IAcceptsMetadata>().LastOrDefault()?.RequestType;
        if (requestType != typeof(ComponentUpdate) && requestType != typeof(BoardUpdate)
            && requestType != typeof(OrderUpdate)) return;

        operation.RequestBody.Content["application/json"].Schema =
            context.SchemaGenerator.GenerateSchema(requestType, context.SchemaRepository);
    }
}
