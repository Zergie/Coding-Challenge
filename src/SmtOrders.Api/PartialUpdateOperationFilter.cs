using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace SmtOrders.Api;

public sealed class PartialUpdateOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.HttpMethod != "PUT") return;
        var requestType = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<IAcceptsMetadata>().LastOrDefault()?.RequestType;
        if (requestType != typeof(ComponentInput) && requestType != typeof(BoardEdit)
            && requestType != typeof(OrderInput)) return;

        var fullSchema = context.SchemaGenerator.GenerateSchema(requestType, context.SchemaRepository);
        var source = fullSchema.Reference is { } reference
            ? context.SchemaRepository.Schemas[reference.Id] : fullSchema;
        operation.RequestBody.Content["application/json"].Schema = new OpenApiSchema
        {
            Type = source.Type,
            Description = source.Description,
            Properties = source.Properties,
            AdditionalPropertiesAllowed = false
        };
    }
}
