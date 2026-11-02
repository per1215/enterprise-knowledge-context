# eShop demo

Runs EKC against Microsoft's reference microservice application [dotnet/eShop](https://github.com/dotnet/eShop) (MIT).
eShop is not copied here; `overlay.yaml` adds the business knowledge that code can't express (processes, rules, domains).

```bash
git clone --depth 1 https://github.com/dotnet/eShop.git ../external/eshop
dotnet run --project ../../src/Ekc.Cli -- map ../external/eshop --overlay overlay.yaml -g eshop.graph.json
dotnet run --project ../../src/Ekc.Cli -- flow "Place an order" -g eshop.graph.json
dotnet run --project ../../src/Ekc.Cli -- impact Order.SetPaidStatus -g eshop.graph.json
```
