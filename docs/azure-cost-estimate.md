# Azure demo cost estimate

Checked **2026-09-25** for **West Europe** (`westeurope`) against Microsoft's public [Azure Retail Prices API](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices). The target subscription ID was supplied privately and is intentionally omitted from this public repository. Azure CLI is not signed in, so subscription-specific free credits, offers, taxes, and restrictions remain unverified.

| Planned resource | Retail meter | Rate | 730-hour month |
| --- | --- | ---: | ---: |
| App Service F1 | Free tier | $0 | $0 |
| PostgreSQL Flexible Server, Standard_B1ms | B1MS compute | $0.0199/hour | $14.5270 |
| PostgreSQL Flexible Server, 32 GB | Storage Data Stored | $0.1369/GB-month | $4.3808 |
| Automated backup, seven days | First 32 GB included | $0 if within allowance | $0 |
| **Estimated total** | | | **$18.9078 ≈ $18.91** |

For a **168-hour** demonstration: `$18.9078 × 168 / 730 = $4.35`, assuming storage is billed proportionally and the resources are deleted at the end. Excess backup is **$0.103/GB-month**. [Microsoft says backup storage up to 100% of provisioned server storage is included](https://learn.microsoft.com/en-us/azure/postgresql/backup-restore/concepts-backup-restore). This small demo is expected to stay below that allowance, but usage must be checked after deployment.

The month estimate **exceeds the $5/month soft target**. Confirm subscription eligibility and cost before creating resources. The one-week estimate is close to $5; allowing resources to remain beyond a week can cross that amount. App Service Free quota and availability are also subject to the selected subscription and region.

To reproduce the retail meter lookup, query `https://prices.azure.com/api/retail/prices?api-version=2023-01-01-preview` with filter `armRegionName eq 'westeurope' and serviceName eq 'Azure Database for PostgreSQL'`, then select Consumption meters `B1MS`, `Storage Data Stored`, and `Backup Storage LRS Data Stored`. Rates are in USD and can change. The [Azure pricing calculator](https://azure.microsoft.com/en-us/pricing/calculator/) provides another check.
