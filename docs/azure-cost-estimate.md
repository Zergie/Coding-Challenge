# Azure demo cost estimate

Checked **2026-09-25** for **West Europe** (`westeurope`) against Microsoft's [Azure Retail Prices API](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices). The design uses a Windows App Service F1 plan and Azure Table Storage in a Standard LRS StorageV2 account. [App Service F1 is free](https://azure.microsoft.com/en-us/pricing/details/app-service/windows/); it has resource quotas and no SLA.

| Resource or meter | Retail rate | Demo assumption | Monthly estimate |
| --- | ---: | ---: | ---: |
| App Service F1 | $0 | One app | $0 |
| Standard LRS Tables, data stored | $0.045 per GB-month | 1 GB-month | $0.0450 |
| Standard LRS Tables, read/write/list/scan/delete/batch operations | $0.00036 per 10,000 operations | 100,000 operations | $0.0036 |
| **Total** | | | **$0.0486 (about $0.05)** |

The estimate is **below the $5/month soft target** under these assumptions. The demo can use less than 1 GB, but catalog scans and the version row add Table operations. Actual charges depend on usage and the subscription's offer, credits, taxes, and any network transfer. Check the subscription's pricing and the Azure pricing calculator before provisioning; monitor resource group Cost Analysis afterward.

To reproduce the Table rates, query `https://prices.azure.com/api/retail/prices` with the filter `armRegionName eq 'westeurope' and serviceName eq 'Storage' and contains(productName, 'Table')`, then select `skuName = Standard LRS` and `type = Consumption`. The returned `LRS Data Stored` rate was `$0.045` per `1 GB/Month`; all listed Standard LRS Table operation meters were `$0.00036` per `10K` at the check date. The earlier **$18.91/month PostgreSQL estimate no longer applies**.
