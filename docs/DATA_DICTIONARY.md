# Data Dictionary — MultifamilyDataHub

## SQL Tables (warehouse schema)

### warehouse.dim_submarket
Dimension table for the 12 tracked US submarkets.

| Column | Type | Description | Key |
|---|---|---|---|
| SubmarketId | INT IDENTITY | Surrogate key | PK |
| Name | NVARCHAR(100) | Submarket name (e.g., "Austin") | |
| State | NVARCHAR(50) | 2-letter state code | |
| Region | NVARCHAR(50) | Geographic region (e.g., "Southeast/Southwest") | |
| CreatedAt | DATETIME2 | Row creation timestamp | |

---

### warehouse.dim_listing
Dimension table representing unique rental unit identities.

| Column | Type | Description | Key |
|---|---|---|---|
| ListingId | UNIQUEIDENTIFIER | Surrogate key (MDH-internal GUID) | PK |
| ExternalId | NVARCHAR(100) | Operator-assigned external identifier | IDX |
| SubmarketId | INT | FK to dim_submarket | FK |
| StreetAddress | NVARCHAR(300) | Street address of the unit | |
| Unit | NVARCHAR(50) | Unit designator (e.g., "#202") | |
| Bedrooms | INT | Number of bedrooms (0=studio) | |
| Bathrooms | DECIMAL(4,1) | Number of bathrooms | |
| Sqft | INT | Unit square footage | |
| Operator | NVARCHAR(200) | Property management company | |
| FirstSeenAt | DATETIME2 | When this listing first appeared | |
| LastUpdatedAt | DATETIME2 | When listing was last refreshed | |
| IsActive | BIT | Whether unit is currently listed | |

---

### warehouse.fact_daily_rent
Fact table recording one rent observation per listing per day.

| Column | Type | Description | Key |
|---|---|---|---|
| FactId | BIGINT IDENTITY | Surrogate key | PK |
| ListingId | UNIQUEIDENTIFIER | FK to dim_listing | FK |
| RentDate | DATE | Date of rent observation (grain = 1 day) | IDX (unique w/ ListingId) |
| AskingRent | DECIMAL(10,2) | Published asking rent in USD | |
| EffectiveRent | DECIMAL(10,2) | Net rent after concessions | |
| Concessions | DECIMAL(10,2) | Dollar value of concessions offered | |
| RentPerSqft | DECIMAL(6,2) | Effective rent divided by square footage | |
| LoadedAt | DATETIME2 | ETL load timestamp | |

**Grain:** One row per listing per calendar day.

---

### warehouse.fact_market_metrics
Fact table with pre-aggregated submarket-level metrics.

| Column | Type | Description | Key |
|---|---|---|---|
| MetricId | BIGINT IDENTITY | Surrogate key | PK |
| SubmarketId | INT | FK to dim_submarket | FK |
| Bedrooms | INT | Bedroom band (0–4+) | IDX (unique combo) |
| MetricDate | DATE | Date of metric computation | IDX (unique combo) |
| AvgRent | DECIMAL(10,2) | Mean effective rent for the band | |
| MedianRent | DECIMAL(10,2) | Median effective rent for the band | |
| RentPerSqft | DECIMAL(6,2) | Average rent-per-sqft | |
| OccupancyEstimate | DECIMAL(5,4) | Estimated occupancy rate (0.80–0.97) | |
| SampleSize | INT | Number of listings in the computation | |
| ComputedAt | DATETIME2 | When the aggregation ran | |

**Grain:** One row per submarket × bedroom band × calendar day.

---

### warehouse.fact_anomaly
Fact table recording listings flagged as statistical outliers.

| Column | Type | Description | Key |
|---|---|---|---|
| AnomalyId | UNIQUEIDENTIFIER | Surrogate key | PK |
| ListingId | UNIQUEIDENTIFIER | FK to dim_listing | FK |
| AskingRent | DECIMAL(10,2) | Rent at time of flagging | |
| SubmarketAvgRent | DECIMAL(10,2) | Submarket mean used in z-score calc | |
| StdDev | DECIMAL(10,2) | Standard deviation of the band | |
| ZScore | DECIMAL(6,3) | (AskingRent - Mean) / StdDev | |
| FlagReason | NVARCHAR(500) | Human-readable explanation | |
| DetectedAt | DATETIME2 | When anomaly was flagged | |
| IsResolved | BIT | Whether anomaly has been reviewed | |

---

## MongoDB Collection

### mdh_raw.listings_raw

Raw scraped listing documents in the landing zone. One document per listing tick.

```json
{
  "_id": { "$oid": "507f1f77bcf86cd799439011" },
  "external_id": "EXT-A3F9B12C7D",
  "submarket": "Austin",
  "street_address": "1234 Lamar Blvd",
  "unit": "#305",
  "bedrooms": 2,
  "bathrooms": 2.0,
  "sqft": 1050,
  "asking_rent": 2350.00,
  "effective_rent": 2200.00,
  "concessions": 150.00,
  "operator": "Greystar",
  "scraped_at": { "$date": "2024-06-15T14:22:11Z" },
  "processed": false,
  "correlation_id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
}
```

**TTL / Cleanup:** In production, a TTL index should be added to `scraped_at` (e.g., 30 days) to prevent unbounded growth.

---

## Reserved Domain Words

| Term | Definition |
|---|---|
| **Submarket** | A named metropolitan micro-market within a major US city used as the primary geographic unit of analysis (e.g., "Austin", "Miami"). Not a zip code or neighborhood — it's a coarse-grained MSA segment used by operators to compare properties. |
| **Asking rent** | The published, advertised monthly rent for a unit before any concessions or incentives are applied. |
| **Effective rent** | The actual economic rent received after concessions are subtracted from asking rent: `asking_rent - concessions`. This is the true cashflow figure. |
| **Concession** | A rent reduction or incentive offered by the operator (e.g., "1 month free"), expressed in monthly dollar terms. |
| **Operator** | The property management company responsible for leasing and managing the apartment community (e.g., Greystar, MAA, Camden). |
| **Dim table** | A dimension table in the star schema. Contains descriptive, slowly-changing attributes used to label and filter fact data (e.g., `dim_listing`, `dim_submarket`). |
| **Fact table** | A fact table in the star schema. Contains measurable, quantitative events at a specific grain (e.g., `fact_daily_rent` captures one rent reading per listing per day). |
| **Landing zone** | The raw, unprocessed data storage layer. In this project: MongoDB `mdh_raw.listings_raw`. Data arrives here first, before any transformation or validation. |
| **Curated zone** | The cleaned, validated, and structured data layer. In this project: SQL Server `warehouse.*` tables. Data here has been normalized, deduped, and enriched. |
| **Star schema** | A dimensional data model design with one central fact table surrounded by dimension tables, resembling a star. Optimized for OLAP queries. |
| **Grain** | The level of detail of a single row in a fact table. `fact_daily_rent` grain = one listing × one calendar day. |

---

## Acronyms

| Acronym | Expansion |
|---|---|
| MDH | MultifamilyDataHub — the name of this project |
| ETL | Extract, Transform, Load — the process of moving data from source to warehouse |
| ELT | Extract, Load, Transform — variation where raw data lands first, transformed later |
| DW | Data Warehouse — the curated SQL Server database in this project |
| SLA | Service Level Agreement — performance or availability guarantee |
| MQL | MongoDB Query Language — the MongoDB document query syntax |
| ADR | Architectural Decision Record — a document capturing a design decision and its rationale |
| CQRS | Command Query Responsibility Segregation — pattern separating read and write models |
| DDD | Domain-Driven Design — design approach centered on the business domain |
| PK | Primary Key — the unique identifier column(s) of a table |
| FK | Foreign Key — a column referencing a PK in another table |
| TTL | Time To Live — automatic document/record expiration policy |
| RBAC | Role-Based Access Control — authorization model based on user roles |
