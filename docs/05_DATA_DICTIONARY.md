# 05 — Data Dictionary

> **Study time:** ~15 minutes
> **Prerequisites:** [`03_DATA_WAREHOUSING_SQLSERVER.md`](./03_DATA_WAREHOUSING_SQLSERVER.md), [`04_NOSQL_LANDING_ZONE_MONGODB.md`](./04_NOSQL_LANDING_ZONE_MONGODB.md)

## Why this matters

Domain vocabulary is tested in every technical interview at a data company. If an interviewer at Smart Apartment Data asks "what is effective rent?" and you say "uh, the rent amount?" you have failed the domain knowledge check. These are the terms their engineering, product, and analytics teams use daily. Knowing them precisely signals that you can work without a glossary attached to every ticket.

By the end of this doc you will be able to: (1) define every domain-specific term used in this codebase from memory; (2) identify the column in each table that corresponds to a given business concept; (3) expand any acronym used in the codebase or architecture.

---

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
| NRT | Nullable Reference Types — C# 8+ compile-time nullability annotations |
| NCI | Non-Clustered Index — SQL Server secondary index stored separately from table data |
| SCD | Slowly Changing Dimension — dimension that changes infrequently over time |
| OLAP | Online Analytical Processing — read-optimized, aggregation-heavy workload |
| OLTP | Online Transaction Processing — write-optimized, transactional workload |
| OCI | Open Container Initiative — standard for container image format and runtime |
| JD | Job Description |

---

## Exercise

1. A colleague says "the grain of fact_daily_rent is per listing." Is that statement complete? What is missing and why does it matter?

2. Open `src/MDH.OrchestrationService/Jobs/CleanListingsJob.cs` line 39. The code computes `rawListing.AskingRent - rawListing.Concessions`. What domain term does this compute? Which column stores the result in `fact_daily_rent`?

3. What is the difference between `asking_rent` and `effective_rent` in a scenario where a 2-bedroom unit advertises $2,500/month with "1 month free" on a 12-month lease?

4. Name three scenarios where `IsResolved = true` would be set on a `fact_anomaly` row and explain who (which process or person) would set it in a production system.

---

## Common mistakes

- **Confusing asking rent with effective rent in queries.** Aggregating `asking_rent` instead of `effective_rent` overstates market rent by the concession amount. For occupancy and demand metrics, `effective_rent` is the correct measure. `asking_rent` is the headline number operators advertise.

- **Treating SubmarketId as a stable natural key.** `SubmarketId` is a surrogate auto-increment identity. Do not hardcode `SubmarketId = 1` for Austin in application code — it is an implementation detail. Look up the submarket by `Name` and let the DB return the ID.

- **Using the MongoDB `_id` as a cross-system identifier.** The MongoDB `ObjectId` in `listings_raw` is MongoDB-internal. The `external_id` field is the business key that crosses the MongoDB→SQL boundary. `ListingId` in SQL is a new GUID that the ETL job mints — it is not derived from the MongoDB `_id`.

- **Omitting the grain from fact table design.** "A fact table for rent data" is underspecified. "A fact table with grain = one listing × one calendar day" is a complete spec that drives the uniqueness constraint, the allowed dimensions, and what GROUP BY means.

- **Using acronyms without defining them in team communications.** SCD, SLA, MQL, and CQRS all mean specific things; using them casually without confirming shared understanding is a source of miscommunication in design documents and code reviews.

---

## Interview Angle — Smart Apartment Data

1. **"What is the difference between asking rent and effective rent?"** — Asking rent is the advertised price. Effective rent is asking rent minus concessions. If a unit is $2,500/month with "1 month free" on a 12-month lease, effective rent = $2,500 × 11/12 = $2,292. Analysts and operators always work with effective rent for economic modeling.

2. **"What does 'grain' mean in a fact table?"** — The grain is the level of detail of a single row. It must be defined explicitly before the table is designed because it determines which dimensions you can attach and what aggregate functions mean. In `fact_daily_rent`, the grain is one listing × one calendar day. Violating the grain — e.g., inserting two rows for the same listing on the same day — is caught by the unique constraint `IX_fact_daily_rent_ListingId_RentDate`.

3. **"What is a submarket in this context?"** — A named metropolitan micro-market used as the primary geographic unit of analysis. Not a zip code, not a neighborhood — a coarse-grained segment of a major metropolitan area that operators use to compare properties. In this system: Austin, Houston, Dallas, Phoenix, Atlanta, Denver, Miami, Nashville, Tampa, Orlando, Raleigh, Charlotte.

4. **30-second talking point:** "The vocabulary matters as much as the code. Effective rent is the economic cash flow, not the headline price. Grain defines what a fact row represents and what GROUP BY means. Landing zone is where raw data arrives before transformation. Curated zone is where it goes after ETL. Understanding these terms precisely is how you read a data ticket without asking follow-up questions."

5. **Job requirement proof:** Domain knowledge — the reserved domain words section demonstrates familiarity with multifamily rental data terminology used by Smart Apartment Data's product and analytics teams.
