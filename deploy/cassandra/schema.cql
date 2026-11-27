-- Keyspace i tabele za Fleet Monitoring. Idempotentno (IF NOT EXISTS).
CREATE KEYSPACE IF NOT EXISTS fleet
  WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1};

USE fleet;

CREATE TABLE IF NOT EXISTS gps_by_truck_day (
  company_id uuid, truck_id uuid, day date, ts timestamp,
  lat double, lon double, heading smallint, trip_id uuid,
  PRIMARY KEY ((company_id, truck_id, day), ts)
) WITH CLUSTERING ORDER BY (ts ASC)
  AND compaction = {'class': 'TimeWindowCompactionStrategy', 'compaction_window_unit': 'DAYS', 'compaction_window_size': 1};

CREATE TABLE IF NOT EXISTS gps_by_trip (
  company_id uuid, trip_id uuid, ts timestamp,
  lat double, lon double, heading smallint,
  PRIMARY KEY ((company_id, trip_id), ts)
);

CREATE TABLE IF NOT EXISTS trips_by_truck (
  company_id uuid, truck_id uuid, month text, start_time timestamp, trip_id uuid,
  driver_id uuid, origin_name text, origin_lat double, origin_lon double,
  dest_name text, dest_lat double, dest_lon double,
  end_time timestamp, distance_km double, duration_s bigint, delivery_status text,
  PRIMARY KEY ((company_id, truck_id, month), start_time, trip_id)
) WITH CLUSTERING ORDER BY (start_time DESC, trip_id ASC);

CREATE TABLE IF NOT EXISTS trips_by_company_month (
  company_id uuid, month text, start_time timestamp, trip_id uuid, truck_id uuid,
  driver_id uuid, origin_name text, dest_name text,
  end_time timestamp, distance_km double, duration_s bigint, delivery_status text,
  PRIMARY KEY ((company_id, month), start_time, trip_id)
) WITH CLUSTERING ORDER BY (start_time DESC, trip_id ASC);

CREATE TABLE IF NOT EXISTS trips_by_id (
  company_id uuid, trip_id uuid, truck_id uuid, driver_id uuid,
  origin_name text, origin_lat double, origin_lon double,
  dest_name text, dest_lat double, dest_lon double,
  start_time timestamp, end_time timestamp, distance_km double, duration_s bigint, delivery_status text,
  PRIMARY KEY ((company_id, trip_id))
);

CREATE TABLE IF NOT EXISTS events_by_truck (
  company_id uuid, truck_id uuid, month text, event_time timestamp, event_id uuid,
  event_type text, driver_id uuid, trip_id uuid, payload map<text, text>,
  PRIMARY KEY ((company_id, truck_id, month), event_time, event_id)
) WITH CLUSTERING ORDER BY (event_time DESC, event_id ASC);

CREATE TABLE IF NOT EXISTS events_by_driver (
  company_id uuid, driver_id uuid, month text, event_time timestamp, event_id uuid,
  event_type text, truck_id uuid, trip_id uuid, payload map<text, text>,
  PRIMARY KEY ((company_id, driver_id, month), event_time, event_id)
) WITH CLUSTERING ORDER BY (event_time DESC, event_id ASC);

CREATE TABLE IF NOT EXISTS events_by_trip (
  company_id uuid, trip_id uuid, event_time timestamp, event_id uuid,
  event_type text, truck_id uuid, driver_id uuid, payload map<text, text>,
  PRIMARY KEY ((company_id, trip_id), event_time, event_id)
);
