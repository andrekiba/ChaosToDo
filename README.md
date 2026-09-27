# ChaosToDo

App demo per la sessione "Chaos Engineering su Azure" (Azure AI Day Torino).

Una piccola API Todo in **.NET 10** con **Aspire** (ultima versione, 13.5.4), pensata per essere
distribuita su **Azure App Service** (multi-zona) + **Azure SQL Database** + **Azure Managed Redis**,
in modo da poter eseguire dal vivo tre Scenari di Azure Chaos Studio:

1. **Compute Zone Down** — App Service su piano Premium v3 zone-redundant, 3 istanze.
2. **SQL DB Failover** — Azure SQL Database Business Critical, zone-redundant (failover automatico
   verso la replica HA nella zona, senza bisogno di un failover group separato).
3. **Cache Stampede** (+ variante *with Process Crash*) — Azure Managed Redis + App Service, con
   [FusionCache](https://github.com/ZiggyCreatures/FusionCache) davanti a Entity Framework Core per
   mostrare la protezione dallo stampede quando la cache viene svuotata.

## Struttura della solution

```
ChaosToDo.AppHost/          Progetto Aspire: modella le risorse Azure e orchestra l'app
ChaosToDo.Api/       API minimale (.NET 10) — Todo CRUD, EF Core, FusionCache
ChaosToDo.ServiceDefaults/  Defaults Aspire (OpenTelemetry, health check, resilienza HTTP)
```

## Eseguire in locale

Serve Docker Desktop (o un altro motore di container) avviato: Aspire in modalità `run` esegue
SQL Server e Redis come container locali grazie a `.RunAsContainer()` nell'AppHost, così non serve
una vera subscription Azure per sviluppare.

```bash
cd ChaosToDo.AppHost
aspire run
```

La dashboard di Aspire si apre in automatico e mostra i log/tracce di API, SQL e Redis. L'endpoint
dell'API espone:

| Endpoint | Descrizione |
|---|---|
| `GET /api/todos` | Lista, **passa da FusionCache** (L1 in memoria + L2 Redis) |
| `GET /api/todos/nocache` | Stessa query, **senza cache** — usalo per mostrare il "prima" |
| `GET /api/todos/{id}` | Singolo item, cache-aside via FusionCache |
| `POST /api/todos` | Crea un item (`{ "title": "..." }`) e invalida la cache |
| `PUT /api/todos/{id}` | Aggiorna e invalida la cache |
| `DELETE /api/todos/{id}` | Cancella e invalida la cache |

Ogni chiamata "fredda" (factory di FusionCache o `/nocache`) ha una latenza simulata di 800&nbsp;ms
(`Demo:SimulatedDbLatencyMs` in `appsettings.json`) apposta per rendere visibile a occhio, o con un
tool di carico, la differenza fra una richiesta che va al database e una servita dalla cache.

## Pubblicare su Azure

```bash
cd ChaosToDo.AppHost
aspire publish   # genera Bicep/manifest, utile per revisionare cosa verrà creato
aspire deploy    # provisiona le risorse Azure e pubblica l'API su App Service
```

`aspire deploy` chiede l'accesso alla subscription Azure (via `az login` / Azure Developer CLI) e
crea: il resource group, il piano App Service Premium v3 zone-redundant, il sito App Service con
l'API, il server + database Azure SQL Business Critical zone-redundant, e la cache Azure Managed
Redis. I nomi delle risorse e la region vengono chiesti in modo interattivo la prima volta.

> Nota: in questo ambiente sandbox non è stato possibile eseguire `aspire run`/`aspire deploy` fino
> in fondo per una limitazione di rete locale del container (il canale di comunicazione interno fra
> CLI e AppHost non riesce ad aprire un socket) — non è legata al codice. Sul tuo PC, con Docker
> attivo, dovrebbe funzionare direttamente; se incontri un problema diverso, dimmi l'errore esatto e
> lo sistemiamo.

## Collegare la demo agli Scenari di Chaos Studio

- **Compute Zone Down**: punta lo Scenario al piano App Service (`app-service-env`) creato da questa
  solution — è già zone-redundant con 3 istanze.
- **SQL DB Failover**: punta lo Scenario al database `database` sul server `sql` — è Business
  Critical e zone-redundant, quindi ha una replica HA su cui forzare il failover.
- **Cache Stampede**: punta lo Scenario alla cache `cache` (Azure Managed Redis) e all'App Service.
  Prima della demo, genera un po' di carico su `GET /api/todos` (es. con `hey` o `bombardier`) per
  scaldare la cache, poi lascia partire lo Scenario: mostra come con FusionCache le richieste
  concorrenti collassano su un'unica chiamata al database invece di travolgerlo. Per il "prima",
  ripeti lo stesso carico su `GET /api/todos/nocache`.

## Personalizzare

- `Demo:SimulatedDbLatencyMs` in `ChaosToDo.Api/appsettings.json` — alza il valore se vuoi
  rendere ancora più evidente la differenza cache/no-cache durante la demo.
- La SKU di Azure SQL (`BC_Gen5`, capacity 2) e del piano App Service (`P1v3`, capacity 3) sono
  configurate in `ChaosToDo.AppHost/AppHost.cs` — riducile se vuoi contenere i costi al di fuori
  della demo.
