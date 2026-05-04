using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace WarpTalk.BillingService.API.Swagger;

public sealed class BillingSwaggerExamplesOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath?.Split('?')[0];
        if (string.IsNullOrWhiteSpace(path) || operation.RequestBody is null)
        {
            return;
        }

        switch (path)
        {
            case "api/v1/billing/checkout":
            case "api/v1/billing/transaction/create-link":
            case "api/v1/billing/transaction/create-link-owner":
                SetExample(operation, CreatePaymentLinkExample());
                break;

            case "api/v1/billing/quota/topup":
                SetExample(operation, JsonNode.Parse("10000")!);
                break;

            case "api/v1/billing/quota/upgrade":
            case "api/v1/billing/subscription/upgrade":
                SetExample(operation, JsonNode.Parse("\"3fa85f64-5717-4562-b3fc-2c963f66afa6\"")!);
                break;

            case "api/admin/billing/subscription/create":
                SetExample(operation, CreateSubscriptionExample());
                break;

            case "api/admin/billing/subscription/upgrade":
                SetExample(operation, UpgradeSubscriptionExample());
                break;

            case "api/v1/billing/webhook/payos":
            case "api/v1/billing/payos/webhook":
            case "api/v1/billing/transaction/webhook/payos":
                SetExample(operation, PayOsWebhookExample());
                break;

            case "api/v1/billing/usage-events":
                SetExample(operation, UsageEventExample());
                break;
        }
    }

    private static void SetExample(OpenApiOperation operation, JsonNode example)
    {
        foreach (var content in operation.RequestBody!.Content.Values)
        {
            content.Example = example;
        }
    }

        private static JsonNode CreatePaymentLinkExample() =>
                JsonNode.Parse("""
                {
                    "planId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                    "topUpMinutes": 10000
                }
                """)!;

        private static JsonNode CreateSubscriptionExample() =>
                JsonNode.Parse("""
                {
                    "workspaceId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                    "planId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                    "ownerUserId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                    "startDate": "2026-05-04T12:33:10.278Z",
                    "durationDays": 30
                }
                """)!;

        private static JsonNode UpgradeSubscriptionExample() =>
                JsonNode.Parse("""
                {
                    "subscriptionId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                    "newPlanId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                    "ownerUserId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                    "upgradedAt": "2026-05-04T12:33:10.279Z"
                }
                """)!;

        private static JsonNode UsageEventExample() =>
                JsonNode.Parse("""
                {
                    "workspaceId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                    "eventType": "token_usage",
                    "provider": "openai",
                    "usage": {
                        "promptTokens": 1200,
                        "completionTokens": 800,
                        "minutes": 2
                    },
                    "occurredAt": "2026-05-04T12:33:10.280Z"
                }
                """)!;

        private static JsonNode PayOsWebhookExample() =>
                JsonNode.Parse("""
                {
                    "code": "00",
                    "desc": "success",
                    "data": {
                        "orderCode": 1234567890123,
                        "amount": 200000,
                        "description": "WarpTalk Pro Upgrade",
                        "accountNumber": "0123456789",
                        "reference": "REF-20260504-001",
                        "transactionDateTime": "2026-05-04 12:33:10",
                        "currency": "VND",
                        "paymentLinkId": "plink_123",
                        "code": "00",
                        "desc": "success"
                    },
                    "signature": "38475834759348759348759348"
                }
                """)!;
}