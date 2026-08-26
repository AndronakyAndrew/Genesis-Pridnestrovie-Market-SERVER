--
-- PostgreSQL database dump
--

\restrict VlECEttTYO5ieRFviSxiPJZEiNp0qMLYFukBif73fjeNzIerUOgpKMslI6u1pdc

-- Dumped from database version 17.10
-- Dumped by pg_dump version 17.10

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: pg_trgm; Type: EXTENSION; Schema: -; Owner: -
--

CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public;


--
-- Name: EXTENSION pg_trgm; Type: COMMENT; Schema: -; Owner: 
--

COMMENT ON EXTENSION pg_trgm IS 'text similarity measurement and index searching based on trigrams';


--
-- Name: category; Type: TYPE; Schema: public; Owner: genesis
--

CREATE TYPE public.category AS ENUM (
    'realestate',
    'transport',
    'electronics',
    'home',
    'fashion',
    'kids',
    'work',
    'services',
    'animals',
    'other'
);


ALTER TYPE public.category OWNER TO genesis;

--
-- Name: city; Type: TYPE; Schema: public; Owner: genesis
--

CREATE TYPE public.city AS ENUM (
    'tiraspol',
    'bendery',
    'rybnitsa',
    'dubossary',
    'slobodzea',
    'grigoriopol',
    'dnestrovsk'
);


ALTER TYPE public.city OWNER TO genesis;

--
-- Name: condition; Type: TYPE; Schema: public; Owner: genesis
--

CREATE TYPE public.condition AS ENUM (
    'new',
    'used',
    'notapplicable'
);


ALTER TYPE public.condition OWNER TO genesis;

--
-- Name: listing_status; Type: TYPE; Schema: public; Owner: genesis
--

CREATE TYPE public.listing_status AS ENUM (
    'draft',
    'pendingreview',
    'active',
    'sold',
    'archived',
    'rejected'
);


ALTER TYPE public.listing_status OWNER TO genesis;

--
-- Name: price_type; Type: TYPE; Schema: public; Owner: genesis
--

CREATE TYPE public.price_type AS ENUM (
    'fixed',
    'negotiable',
    'free'
);


ALTER TYPE public.price_type OWNER TO genesis;

--
-- Name: favorites_count_sync(); Type: FUNCTION; Schema: public; Owner: genesis
--

CREATE FUNCTION public.favorites_count_sync() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF (TG_OP = 'INSERT') THEN
        UPDATE listings SET "FavoritesCount" = "FavoritesCount" + 1 WHERE "Id" = NEW."ListingId";
    ELSIF (TG_OP = 'DELETE') THEN
        UPDATE listings SET "FavoritesCount" = "FavoritesCount" - 1 WHERE "Id" = OLD."ListingId";
    END IF;
    RETURN NULL;
END;
$$;


ALTER FUNCTION public.favorites_count_sync() OWNER TO genesis;

--
-- Name: reviews_rating_sync(); Type: FUNCTION; Schema: public; Owner: genesis
--

CREATE FUNCTION public.reviews_rating_sync() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    target uuid := COALESCE(NEW."TargetUserId", OLD."TargetUserId");
BEGIN
    UPDATE users u SET
        "ReviewsCount" = agg.cnt,
        "AverageRating" = agg.avg
    FROM (
        SELECT
            COUNT(*)::int AS cnt,
            CASE WHEN COUNT(*) = 0 THEN NULL
                 ELSE round(avg("Rating")::numeric, 2)::double precision END AS avg
        FROM reviews
        WHERE "TargetUserId" = target AND "IsHidden" = false
    ) agg
    WHERE u."Id" = target;
    RETURN NULL;
END;
$$;


ALTER FUNCTION public.reviews_rating_sync() OWNER TO genesis;

SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: __EFMigrationsHistory; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL
);


ALTER TABLE public."__EFMigrationsHistory" OWNER TO genesis;

--
-- Name: contact_reveals; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.contact_reveals (
    "Id" uuid NOT NULL,
    "ListingId" uuid NOT NULL,
    "ViewerUserId" uuid,
    "IpHash" character varying(64) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone
);


ALTER TABLE public.contact_reveals OWNER TO genesis;

--
-- Name: conversations; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.conversations (
    "Id" uuid NOT NULL,
    "ListingId" uuid NOT NULL,
    "BuyerId" uuid NOT NULL,
    "SellerId" uuid NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "LastMessageAt" timestamp with time zone
);


ALTER TABLE public.conversations OWNER TO genesis;

--
-- Name: favorites; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.favorites (
    "UserId" uuid NOT NULL,
    "ListingId" uuid NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL
);


ALTER TABLE public.favorites OWNER TO genesis;

--
-- Name: listing_images; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.listing_images (
    "Id" uuid NOT NULL,
    "ListingId" uuid NOT NULL,
    "ObjectKey" character varying(512) NOT NULL,
    "ThumbKey" character varying(512) NOT NULL,
    "SortOrder" integer NOT NULL,
    "Width" integer NOT NULL,
    "Height" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL
);


ALTER TABLE public.listing_images OWNER TO genesis;

--
-- Name: listings; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.listings (
    "Id" uuid NOT NULL,
    "Title" character varying(120) NOT NULL,
    "Description" character varying(5000) NOT NULL,
    "Price" numeric(12,0),
    "PriceType" public.price_type NOT NULL,
    "Category" public.category NOT NULL,
    "SubcategoryId" integer NOT NULL,
    "City" public.city NOT NULL,
    "District" character varying(100),
    "Condition" public.condition NOT NULL,
    "Status" public.listing_status NOT NULL,
    "ViewsCount" integer DEFAULT 0 NOT NULL,
    "PublishedAt" timestamp with time zone,
    "DeletedAt" timestamp with time zone,
    "OwnerId" uuid NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone,
    "Slug" character varying(160) DEFAULT ''::character varying NOT NULL,
    "SearchVector" tsvector GENERATED ALWAYS AS ((setweight(to_tsvector('russian'::regconfig, (COALESCE("Title", ''::character varying))::text), 'A'::"char") || setweight(to_tsvector('russian'::regconfig, (COALESCE("Description", ''::character varying))::text), 'B'::"char"))) STORED NOT NULL,
    "FavoritesCount" integer DEFAULT 0 NOT NULL,
    "ModerationPriority" integer DEFAULT 0 NOT NULL,
    "ArchiveWarningAt" timestamp with time zone,
    "ArchivedAt" timestamp with time zone,
    "BumpedAt" timestamp with time zone,
    "SoldAt" timestamp with time zone,
    CONSTRAINT ck_listings_description_length CHECK ((char_length(("Description")::text) <= 5000)),
    CONSTRAINT ck_listings_district_length CHECK ((("District" IS NULL) OR (char_length(("District")::text) <= 100))),
    CONSTRAINT ck_listings_favorites_nonnegative CHECK (("FavoritesCount" >= 0)),
    CONSTRAINT ck_listings_price_nonnegative CHECK ((("Price" IS NULL) OR ("Price" >= (0)::numeric))),
    CONSTRAINT ck_listings_price_pricetype CHECK (((("PriceType" = 'free'::public.price_type) AND ("Price" = (0)::numeric)) OR (("PriceType" = 'negotiable'::public.price_type) AND ("Price" IS NULL)) OR (("PriceType" = 'fixed'::public.price_type) AND ("Price" IS NOT NULL) AND ("Price" >= (0)::numeric)))),
    CONSTRAINT ck_listings_title_length CHECK (((char_length(("Title")::text) >= 5) AND (char_length(("Title")::text) <= 120))),
    CONSTRAINT ck_listings_views_nonnegative CHECK (("ViewsCount" >= 0))
);


ALTER TABLE public.listings OWNER TO genesis;

--
-- Name: messages; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.messages (
    "Id" uuid NOT NULL,
    "ConversationId" uuid NOT NULL,
    "SenderId" uuid NOT NULL,
    "Text" character varying(2000) NOT NULL,
    "IsRead" boolean NOT NULL,
    "IsDeleted" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT ck_messages_text_length CHECK ((char_length(("Text")::text) <= 2000))
);


ALTER TABLE public.messages OWNER TO genesis;

--
-- Name: moderation_logs; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.moderation_logs (
    "Id" uuid NOT NULL,
    "ActorId" uuid NOT NULL,
    "Action" character varying(40) NOT NULL,
    "TargetType" character varying(20) NOT NULL,
    "TargetId" uuid NOT NULL,
    "Reason" character varying(500),
    "PayloadJson" jsonb,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT ck_moderation_logs_reason_length CHECK ((("Reason" IS NULL) OR (char_length(("Reason")::text) <= 500)))
);


ALTER TABLE public.moderation_logs OWNER TO genesis;

--
-- Name: outbox_messages; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.outbox_messages (
    "Id" uuid NOT NULL,
    "Type" character varying(64) NOT NULL,
    "Payload" character varying(4096) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ProcessedAt" timestamp with time zone,
    "Attempts" integer NOT NULL,
    "Error" character varying(2048),
    "NextAttemptAt" timestamp with time zone DEFAULT now() NOT NULL,
    "Status" character varying(16) DEFAULT 'Pending'::character varying NOT NULL
);


ALTER TABLE public.outbox_messages OWNER TO genesis;

--
-- Name: profiles; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.profiles (
    "UserId" uuid NOT NULL,
    "DisplayName" character varying(60) NOT NULL,
    "City" public.city NOT NULL,
    "AvatarUrl" character varying(512),
    "TelegramUsername" character varying(64),
    "ViberEnabled" boolean DEFAULT false NOT NULL,
    "WhatsappEnabled" boolean DEFAULT false NOT NULL,
    "ShowPhoneInListing" boolean DEFAULT true NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone,
    "NotifyVia" character varying(16) DEFAULT 'Email'::character varying NOT NULL,
    "TelegramChatId" bigint,
    CONSTRAINT ck_profiles_display_name_length CHECK ((char_length(("DisplayName")::text) <= 60))
);


ALTER TABLE public.profiles OWNER TO genesis;

--
-- Name: qrtz_blob_triggers; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.qrtz_blob_triggers (
    sched_name text NOT NULL,
    trigger_name text NOT NULL,
    trigger_group text NOT NULL,
    blob_data bytea
);


ALTER TABLE public.qrtz_blob_triggers OWNER TO genesis;

--
-- Name: qrtz_calendars; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.qrtz_calendars (
    sched_name text NOT NULL,
    calendar_name text NOT NULL,
    calendar bytea NOT NULL
);


ALTER TABLE public.qrtz_calendars OWNER TO genesis;

--
-- Name: qrtz_cron_triggers; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.qrtz_cron_triggers (
    sched_name text NOT NULL,
    trigger_name text NOT NULL,
    trigger_group text NOT NULL,
    cron_expression text NOT NULL,
    time_zone_id text
);


ALTER TABLE public.qrtz_cron_triggers OWNER TO genesis;

--
-- Name: qrtz_fired_triggers; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.qrtz_fired_triggers (
    sched_name text NOT NULL,
    entry_id text NOT NULL,
    trigger_name text NOT NULL,
    trigger_group text NOT NULL,
    instance_name text NOT NULL,
    fired_time bigint NOT NULL,
    sched_time bigint NOT NULL,
    priority integer NOT NULL,
    state text NOT NULL,
    job_name text,
    job_group text,
    is_nonconcurrent boolean,
    requests_recovery boolean
);


ALTER TABLE public.qrtz_fired_triggers OWNER TO genesis;

--
-- Name: qrtz_job_details; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.qrtz_job_details (
    sched_name text NOT NULL,
    job_name text NOT NULL,
    job_group text NOT NULL,
    description text,
    job_class_name text NOT NULL,
    is_durable boolean NOT NULL,
    is_nonconcurrent boolean NOT NULL,
    is_update_data boolean NOT NULL,
    requests_recovery boolean NOT NULL,
    job_data bytea
);


ALTER TABLE public.qrtz_job_details OWNER TO genesis;

--
-- Name: qrtz_locks; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.qrtz_locks (
    sched_name text NOT NULL,
    lock_name text NOT NULL
);


ALTER TABLE public.qrtz_locks OWNER TO genesis;

--
-- Name: qrtz_paused_trigger_grps; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.qrtz_paused_trigger_grps (
    sched_name text NOT NULL,
    trigger_group text NOT NULL
);


ALTER TABLE public.qrtz_paused_trigger_grps OWNER TO genesis;

--
-- Name: qrtz_scheduler_state; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.qrtz_scheduler_state (
    sched_name text NOT NULL,
    instance_name text NOT NULL,
    last_checkin_time bigint NOT NULL,
    checkin_interval bigint NOT NULL
);


ALTER TABLE public.qrtz_scheduler_state OWNER TO genesis;

--
-- Name: qrtz_simple_triggers; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.qrtz_simple_triggers (
    sched_name text NOT NULL,
    trigger_name text NOT NULL,
    trigger_group text NOT NULL,
    repeat_count bigint NOT NULL,
    repeat_interval bigint NOT NULL,
    times_triggered bigint NOT NULL
);


ALTER TABLE public.qrtz_simple_triggers OWNER TO genesis;

--
-- Name: qrtz_simprop_triggers; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.qrtz_simprop_triggers (
    sched_name text NOT NULL,
    trigger_name text NOT NULL,
    trigger_group text NOT NULL,
    str_prop_1 text,
    str_prop_2 text,
    str_prop_3 text,
    int_prop_1 integer,
    int_prop_2 integer,
    long_prop_1 bigint,
    long_prop_2 bigint,
    dec_prop_1 numeric,
    dec_prop_2 numeric,
    bool_prop_1 boolean,
    bool_prop_2 boolean,
    time_zone_id text
);


ALTER TABLE public.qrtz_simprop_triggers OWNER TO genesis;

--
-- Name: qrtz_triggers; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.qrtz_triggers (
    sched_name text NOT NULL,
    trigger_name text NOT NULL,
    trigger_group text NOT NULL,
    job_name text NOT NULL,
    job_group text NOT NULL,
    description text,
    next_fire_time bigint,
    prev_fire_time bigint,
    priority integer,
    trigger_state text NOT NULL,
    trigger_type text NOT NULL,
    start_time bigint NOT NULL,
    end_time bigint,
    calendar_name text,
    misfire_instr smallint,
    job_data bytea
);


ALTER TABLE public.qrtz_triggers OWNER TO genesis;

--
-- Name: refresh_tokens; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.refresh_tokens (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "TokenHash" bytea NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "RevokedAt" timestamp with time zone,
    "ReplacedByTokenId" uuid,
    "CreatedByIpHash" character varying(128)
);


ALTER TABLE public.refresh_tokens OWNER TO genesis;

--
-- Name: reports; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.reports (
    "Id" uuid NOT NULL,
    "TargetType" character varying(20) NOT NULL,
    "TargetId" uuid NOT NULL,
    "ReporterId" uuid,
    "ReporterIpHash" character varying(64),
    "Reason" character varying(20) NOT NULL,
    "Comment" character varying(500),
    "Status" character varying(20) DEFAULT 'New'::character varying NOT NULL,
    "ResolvedByUserId" uuid,
    "ResolvedAt" timestamp with time zone,
    "Resolution" character varying(1000),
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone,
    CONSTRAINT ck_reports_comment_length CHECK ((("Comment" IS NULL) OR (char_length(("Comment")::text) <= 500)))
);


ALTER TABLE public.reports OWNER TO genesis;

--
-- Name: reviews; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.reviews (
    "Id" uuid NOT NULL,
    "ListingId" uuid NOT NULL,
    "AuthorId" uuid NOT NULL,
    "TargetUserId" uuid NOT NULL,
    "Rating" integer NOT NULL,
    "Text" character varying(1000) NOT NULL,
    "IsHidden" boolean DEFAULT false NOT NULL,
    "HiddenByUserId" uuid,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone,
    CONSTRAINT ck_reviews_rating_range CHECK ((("Rating" >= 1) AND ("Rating" <= 5))),
    CONSTRAINT ck_reviews_text_length CHECK ((char_length(("Text")::text) <= 1000))
);


ALTER TABLE public.reviews OWNER TO genesis;

--
-- Name: search_misses; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.search_misses (
    "Id" uuid NOT NULL,
    "Query" character varying(100) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone
);


ALTER TABLE public.search_misses OWNER TO genesis;

--
-- Name: subcategories; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.subcategories (
    "Id" integer NOT NULL,
    "Category" public.category NOT NULL,
    "Slug" character varying(60) NOT NULL,
    "Name" character varying(60) NOT NULL
);


ALTER TABLE public.subcategories OWNER TO genesis;

--
-- Name: users; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.users (
    "Id" uuid NOT NULL,
    "Email" character varying(256) NOT NULL,
    "PasswordHash" character varying(200) NOT NULL,
    "Role" character varying(20) DEFAULT 'User'::character varying NOT NULL,
    "PhoneE164" character varying(20),
    "PhoneVerified" boolean DEFAULT false NOT NULL,
    "SecurityStamp" uuid NOT NULL,
    "IsBanned" boolean DEFAULT false NOT NULL,
    "BannedUntil" timestamp with time zone,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone,
    "EmailVerified" boolean DEFAULT false NOT NULL,
    "IsDeleted" boolean DEFAULT false NOT NULL,
    "AverageRating" double precision,
    "ReviewsCount" integer DEFAULT 0 NOT NULL
);


ALTER TABLE public.users OWNER TO genesis;

--
-- Name: verification_codes; Type: TABLE; Schema: public; Owner: genesis
--

CREATE TABLE public.verification_codes (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "Channel" character varying(16) NOT NULL,
    "Target" character varying(256) NOT NULL,
    "CodeHash" bytea NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "Attempts" integer NOT NULL,
    "ConsumedAt" timestamp with time zone,
    "CreatedAt" timestamp with time zone NOT NULL
);


ALTER TABLE public.verification_codes OWNER TO genesis;

--
-- Data for Name: __EFMigrationsHistory; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public."__EFMigrationsHistory" ("MigrationId", "ProductVersion") FROM stdin;
20260810073042_InitialSchema	10.0.0
20260810092502_AddAuth	10.0.0
20260810103045_EmailVerificationAndGeneralize	10.0.0
20260810162116_AddUserIsDeleted	10.0.0
20260810164819_AddListingSlug	10.0.0
20260811082140_AddFullTextSearch	10.0.0
20260811124927_AddOutboxMessages	10.0.0
20260811160156_AddContactReveals	10.0.0
20260811163007_AddFavorites	10.0.0
20260811174851_AddTrustLayer	10.0.0
20260811183300_AddModerationLog	10.0.0
20260812033320_AddCatalogHygiene	10.0.0
20260812043056_AddOutboxPipeline	10.0.0
\.


--
-- Data for Name: contact_reveals; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.contact_reveals ("Id", "ListingId", "ViewerUserId", "IpHash", "CreatedAt", "UpdatedAt") FROM stdin;
\.


--
-- Data for Name: conversations; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.conversations ("Id", "ListingId", "BuyerId", "SellerId", "CreatedAt", "LastMessageAt") FROM stdin;
\.


--
-- Data for Name: favorites; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.favorites ("UserId", "ListingId", "CreatedAt") FROM stdin;
\.


--
-- Data for Name: listing_images; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.listing_images ("Id", "ListingId", "ObjectKey", "ThumbKey", "SortOrder", "Width", "Height", "CreatedAt") FROM stdin;
\.


--
-- Data for Name: listings; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.listings ("Id", "Title", "Description", "Price", "PriceType", "Category", "SubcategoryId", "City", "District", "Condition", "Status", "ViewsCount", "PublishedAt", "DeletedAt", "OwnerId", "CreatedAt", "UpdatedAt", "Slug", "FavoritesCount", "ModerationPriority", "ArchiveWarningAt", "ArchivedAt", "BumpedAt", "SoldAt") FROM stdin;
\.


--
-- Data for Name: messages; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.messages ("Id", "ConversationId", "SenderId", "Text", "IsRead", "IsDeleted", "CreatedAt") FROM stdin;
\.


--
-- Data for Name: moderation_logs; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.moderation_logs ("Id", "ActorId", "Action", "TargetType", "TargetId", "Reason", "PayloadJson", "CreatedAt") FROM stdin;
\.


--
-- Data for Name: outbox_messages; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.outbox_messages ("Id", "Type", "Payload", "CreatedAt", "ProcessedAt", "Attempts", "Error", "NextAttemptAt", "Status") FROM stdin;
\.


--
-- Data for Name: profiles; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.profiles ("UserId", "DisplayName", "City", "AvatarUrl", "TelegramUsername", "ViberEnabled", "WhatsappEnabled", "ShowPhoneInListing", "CreatedAt", "UpdatedAt", "NotifyVia", "TelegramChatId") FROM stdin;
\.


--
-- Data for Name: qrtz_blob_triggers; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.qrtz_blob_triggers (sched_name, trigger_name, trigger_group, blob_data) FROM stdin;
\.


--
-- Data for Name: qrtz_calendars; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.qrtz_calendars (sched_name, calendar_name, calendar) FROM stdin;
\.


--
-- Data for Name: qrtz_cron_triggers; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.qrtz_cron_triggers (sched_name, trigger_name, trigger_group, cron_expression, time_zone_id) FROM stdin;
genesis-scheduler	catalog-hygiene-daily	DEFAULT	0 0 3 * * ?	UTC
genesis-scheduler	outbox-cleanup-daily	DEFAULT	0 30 3 * * ?	UTC
\.


--
-- Data for Name: qrtz_fired_triggers; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.qrtz_fired_triggers (sched_name, entry_id, trigger_name, trigger_group, instance_name, fired_time, sched_time, priority, state, job_name, job_group, is_nonconcurrent, requests_recovery) FROM stdin;
genesis-scheduler	10e0f83f50e3639221065990720975639221065998131688	outbox-dispatch-interval	DEFAULT	10e0f83f50e3639221065990720975	639221237584370242	639221237684146833	5	ACQUIRED	\N	\N	f	f
\.


--
-- Data for Name: qrtz_job_details; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.qrtz_job_details (sched_name, job_name, job_group, description, job_class_name, is_durable, is_nonconcurrent, is_update_data, requests_recovery, job_data) FROM stdin;
genesis-scheduler	catalog-hygiene	DEFAULT	Автоархивация объявлений и предупреждения авторов	GenesisMarket.Infrastructure.Scheduling.CatalogHygieneJob, GenesisMarket.Infrastructure	t	t	f	f	\N
genesis-scheduler	outbox-dispatch	DEFAULT	Доставка сообщений outbox (email/Telegram/хранилище)	GenesisMarket.Infrastructure.Scheduling.OutboxDispatchJob, GenesisMarket.Infrastructure	t	t	f	f	\N
genesis-scheduler	outbox-cleanup	DEFAULT	Удаление доставленных сообщений outbox старше срока хранения	GenesisMarket.Infrastructure.Scheduling.OutboxCleanupJob, GenesisMarket.Infrastructure	t	t	f	f	\N
\.


--
-- Data for Name: qrtz_locks; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.qrtz_locks (sched_name, lock_name) FROM stdin;
genesis-scheduler	TRIGGER_ACCESS
genesis-scheduler	STATE_ACCESS
\.


--
-- Data for Name: qrtz_paused_trigger_grps; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.qrtz_paused_trigger_grps (sched_name, trigger_group) FROM stdin;
\.


--
-- Data for Name: qrtz_scheduler_state; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.qrtz_scheduler_state (sched_name, instance_name, last_checkin_time, checkin_interval) FROM stdin;
genesis-scheduler	10e0f83f50e3639221065990720975	639221237618913951	7500
\.


--
-- Data for Name: qrtz_simple_triggers; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.qrtz_simple_triggers (sched_name, trigger_name, trigger_group, repeat_count, repeat_interval, times_triggered) FROM stdin;
genesis-scheduler	outbox-dispatch-interval	DEFAULT	-1	10000	1717
\.


--
-- Data for Name: qrtz_simprop_triggers; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.qrtz_simprop_triggers (sched_name, trigger_name, trigger_group, str_prop_1, str_prop_2, str_prop_3, int_prop_1, int_prop_2, long_prop_1, long_prop_2, dec_prop_1, dec_prop_2, bool_prop_1, bool_prop_2, time_zone_id) FROM stdin;
\.


--
-- Data for Name: qrtz_triggers; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.qrtz_triggers (sched_name, trigger_name, trigger_group, job_name, job_group, description, next_fire_time, prev_fire_time, priority, trigger_state, trigger_type, start_time, end_time, calendar_name, misfire_instr, job_data) FROM stdin;
genesis-scheduler	catalog-hygiene-daily	DEFAULT	catalog-hygiene	DEFAULT	\N	639221868000000000	\N	5	WAITING	CRON	639221065980000000	\N	\N	2	\N
genesis-scheduler	outbox-cleanup-daily	DEFAULT	outbox-cleanup	DEFAULT	\N	639221886000000000	\N	5	WAITING	CRON	639221065980000000	\N	\N	2	\N
genesis-scheduler	outbox-dispatch-interval	DEFAULT	outbox-dispatch	DEFAULT	\N	639221237684146833	639221237584146833	5	ACQUIRED	SIMPLE	639221065984146833	\N	\N	4	\N
\.


--
-- Data for Name: refresh_tokens; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.refresh_tokens ("Id", "UserId", "TokenHash", "ExpiresAt", "CreatedAt", "RevokedAt", "ReplacedByTokenId", "CreatedByIpHash") FROM stdin;
\.


--
-- Data for Name: reports; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.reports ("Id", "TargetType", "TargetId", "ReporterId", "ReporterIpHash", "Reason", "Comment", "Status", "ResolvedByUserId", "ResolvedAt", "Resolution", "CreatedAt", "UpdatedAt") FROM stdin;
\.


--
-- Data for Name: reviews; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.reviews ("Id", "ListingId", "AuthorId", "TargetUserId", "Rating", "Text", "IsHidden", "HiddenByUserId", "CreatedAt", "UpdatedAt") FROM stdin;
\.


--
-- Data for Name: search_misses; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.search_misses ("Id", "Query", "CreatedAt", "UpdatedAt") FROM stdin;
\.


--
-- Data for Name: subcategories; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.subcategories ("Id", "Category", "Slug", "Name") FROM stdin;
1	realestate	kvartiry	Квартиры
2	realestate	doma	Дома, дачи, коттеджи
3	realestate	komnaty	Комнаты
4	realestate	zemelnye-uchastki	Земельные участки
5	realestate	garazhi	Гаражи и машиноместа
6	realestate	kommercheskaya	Коммерческая недвижимость
7	transport	legkovye-avto	Легковые автомобили
8	transport	gruzoviki	Грузовики и спецтехника
9	transport	mototsikly	Мотоциклы и мототехника
10	transport	zapchasti	Запчасти и аксессуары
11	transport	vodnyy-transport	Водный транспорт
12	electronics	telefony	Телефоны
13	electronics	noutbuki	Ноутбуки
14	electronics	kompyutery	Компьютеры и комплектующие
15	electronics	tv	Телевизоры и проекторы
16	electronics	foto-video	Фото и видео
17	electronics	audio	Аудиотехника
18	home	mebel	Мебель
19	home	bytovaya-tehnika	Бытовая техника
20	home	remont-stroyka	Ремонт и стройка
21	home	sad-ogorod	Сад и огород
22	home	posuda	Посуда и товары для дома
23	fashion	muzhskaya-odezhda	Мужская одежда
24	fashion	zhenskaya-odezhda	Женская одежда
25	fashion	obuv	Обувь
26	fashion	aksessuary	Аксессуары
27	kids	detskaya-odezhda	Детская одежда и обувь
28	kids	igrushki	Игрушки
29	kids	kolyaski	Коляски
30	kids	detskaya-mebel	Детская мебель
31	work	vakansii	Вакансии
32	work	rezume	Резюме
33	services	stroitelstvo-remont	Строительство и ремонт
34	services	krasota-zdorovie	Красота и здоровье
35	services	obuchenie	Обучение и курсы
36	services	perevozki	Перевозки и грузчики
37	services	remont-tehniki	Ремонт техники
38	animals	sobaki	Собаки
39	animals	koshki	Кошки
40	animals	ptitsy	Птицы
41	animals	tovary-dlya-zhivotnyh	Товары для животных
42	other	raznoe	Разное
\.


--
-- Data for Name: users; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.users ("Id", "Email", "PasswordHash", "Role", "PhoneE164", "PhoneVerified", "SecurityStamp", "IsBanned", "BannedUntil", "CreatedAt", "UpdatedAt", "EmailVerified", "IsDeleted", "AverageRating", "ReviewsCount") FROM stdin;
\.


--
-- Data for Name: verification_codes; Type: TABLE DATA; Schema: public; Owner: genesis
--

COPY public.verification_codes ("Id", "UserId", "Channel", "Target", "CodeHash", "ExpiresAt", "Attempts", "ConsumedAt", "CreatedAt") FROM stdin;
\.


--
-- Name: __EFMigrationsHistory PK___EFMigrationsHistory; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public."__EFMigrationsHistory"
    ADD CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId");


--
-- Name: contact_reveals PK_contact_reveals; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.contact_reveals
    ADD CONSTRAINT "PK_contact_reveals" PRIMARY KEY ("Id");


--
-- Name: conversations PK_conversations; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.conversations
    ADD CONSTRAINT "PK_conversations" PRIMARY KEY ("Id");


--
-- Name: favorites PK_favorites; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.favorites
    ADD CONSTRAINT "PK_favorites" PRIMARY KEY ("UserId", "ListingId");


--
-- Name: listing_images PK_listing_images; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.listing_images
    ADD CONSTRAINT "PK_listing_images" PRIMARY KEY ("Id");


--
-- Name: listings PK_listings; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.listings
    ADD CONSTRAINT "PK_listings" PRIMARY KEY ("Id");


--
-- Name: messages PK_messages; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.messages
    ADD CONSTRAINT "PK_messages" PRIMARY KEY ("Id");


--
-- Name: moderation_logs PK_moderation_logs; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.moderation_logs
    ADD CONSTRAINT "PK_moderation_logs" PRIMARY KEY ("Id");


--
-- Name: outbox_messages PK_outbox_messages; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.outbox_messages
    ADD CONSTRAINT "PK_outbox_messages" PRIMARY KEY ("Id");


--
-- Name: profiles PK_profiles; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.profiles
    ADD CONSTRAINT "PK_profiles" PRIMARY KEY ("UserId");


--
-- Name: refresh_tokens PK_refresh_tokens; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.refresh_tokens
    ADD CONSTRAINT "PK_refresh_tokens" PRIMARY KEY ("Id");


--
-- Name: reports PK_reports; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.reports
    ADD CONSTRAINT "PK_reports" PRIMARY KEY ("Id");


--
-- Name: reviews PK_reviews; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.reviews
    ADD CONSTRAINT "PK_reviews" PRIMARY KEY ("Id");


--
-- Name: search_misses PK_search_misses; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.search_misses
    ADD CONSTRAINT "PK_search_misses" PRIMARY KEY ("Id");


--
-- Name: subcategories PK_subcategories; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.subcategories
    ADD CONSTRAINT "PK_subcategories" PRIMARY KEY ("Id");


--
-- Name: users PK_users; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.users
    ADD CONSTRAINT "PK_users" PRIMARY KEY ("Id");


--
-- Name: verification_codes PK_verification_codes; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.verification_codes
    ADD CONSTRAINT "PK_verification_codes" PRIMARY KEY ("Id");


--
-- Name: qrtz_blob_triggers qrtz_blob_triggers_pkey; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_blob_triggers
    ADD CONSTRAINT qrtz_blob_triggers_pkey PRIMARY KEY (sched_name, trigger_name, trigger_group);


--
-- Name: qrtz_calendars qrtz_calendars_pkey; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_calendars
    ADD CONSTRAINT qrtz_calendars_pkey PRIMARY KEY (sched_name, calendar_name);


--
-- Name: qrtz_cron_triggers qrtz_cron_triggers_pkey; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_cron_triggers
    ADD CONSTRAINT qrtz_cron_triggers_pkey PRIMARY KEY (sched_name, trigger_name, trigger_group);


--
-- Name: qrtz_fired_triggers qrtz_fired_triggers_pkey; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_fired_triggers
    ADD CONSTRAINT qrtz_fired_triggers_pkey PRIMARY KEY (sched_name, entry_id);


--
-- Name: qrtz_job_details qrtz_job_details_pkey; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_job_details
    ADD CONSTRAINT qrtz_job_details_pkey PRIMARY KEY (sched_name, job_name, job_group);


--
-- Name: qrtz_locks qrtz_locks_pkey; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_locks
    ADD CONSTRAINT qrtz_locks_pkey PRIMARY KEY (sched_name, lock_name);


--
-- Name: qrtz_paused_trigger_grps qrtz_paused_trigger_grps_pkey; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_paused_trigger_grps
    ADD CONSTRAINT qrtz_paused_trigger_grps_pkey PRIMARY KEY (sched_name, trigger_group);


--
-- Name: qrtz_scheduler_state qrtz_scheduler_state_pkey; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_scheduler_state
    ADD CONSTRAINT qrtz_scheduler_state_pkey PRIMARY KEY (sched_name, instance_name);


--
-- Name: qrtz_simple_triggers qrtz_simple_triggers_pkey; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_simple_triggers
    ADD CONSTRAINT qrtz_simple_triggers_pkey PRIMARY KEY (sched_name, trigger_name, trigger_group);


--
-- Name: qrtz_simprop_triggers qrtz_simprop_triggers_pkey; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_simprop_triggers
    ADD CONSTRAINT qrtz_simprop_triggers_pkey PRIMARY KEY (sched_name, trigger_name, trigger_group);


--
-- Name: qrtz_triggers qrtz_triggers_pkey; Type: CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_triggers
    ADD CONSTRAINT qrtz_triggers_pkey PRIMARY KEY (sched_name, trigger_name, trigger_group);


--
-- Name: IX_contact_reveals_IpHash_CreatedAt; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_contact_reveals_IpHash_CreatedAt" ON public.contact_reveals USING btree ("IpHash", "CreatedAt");


--
-- Name: IX_contact_reveals_ListingId; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_contact_reveals_ListingId" ON public.contact_reveals USING btree ("ListingId");


--
-- Name: IX_conversations_BuyerId; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_conversations_BuyerId" ON public.conversations USING btree ("BuyerId");


--
-- Name: IX_conversations_ListingId_BuyerId; Type: INDEX; Schema: public; Owner: genesis
--

CREATE UNIQUE INDEX "IX_conversations_ListingId_BuyerId" ON public.conversations USING btree ("ListingId", "BuyerId");


--
-- Name: IX_conversations_SellerId; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_conversations_SellerId" ON public.conversations USING btree ("SellerId");


--
-- Name: IX_favorites_ListingId; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_favorites_ListingId" ON public.favorites USING btree ("ListingId");


--
-- Name: IX_favorites_UserId_CreatedAt_ListingId; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_favorites_UserId_CreatedAt_ListingId" ON public.favorites USING btree ("UserId", "CreatedAt" DESC, "ListingId" DESC);


--
-- Name: IX_listing_images_ListingId_SortOrder; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_listing_images_ListingId_SortOrder" ON public.listing_images USING btree ("ListingId", "SortOrder");


--
-- Name: IX_listings_Category_City_Status; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_listings_Category_City_Status" ON public.listings USING btree ("Category", "City", "Status") WHERE ("DeletedAt" IS NULL);


--
-- Name: IX_listings_OwnerId_Status; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_listings_OwnerId_Status" ON public.listings USING btree ("OwnerId", "Status");


--
-- Name: IX_listings_SearchVector; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_listings_SearchVector" ON public.listings USING gin ("SearchVector");


--
-- Name: IX_listings_Slug; Type: INDEX; Schema: public; Owner: genesis
--

CREATE UNIQUE INDEX "IX_listings_Slug" ON public.listings USING btree ("Slug");


--
-- Name: IX_listings_Status_CreatedAt; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_listings_Status_CreatedAt" ON public.listings USING btree ("Status", "CreatedAt" DESC) WHERE ("DeletedAt" IS NULL);


--
-- Name: IX_listings_SubcategoryId; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_listings_SubcategoryId" ON public.listings USING btree ("SubcategoryId");


--
-- Name: IX_listings_bump_active; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_listings_bump_active" ON public.listings USING btree (COALESCE("BumpedAt", "PublishedAt", "CreatedAt") DESC, "Id" DESC) WHERE (("Status" = 'active'::public.listing_status) AND ("DeletedAt" IS NULL));


--
-- Name: IX_messages_ConversationId_CreatedAt; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_messages_ConversationId_CreatedAt" ON public.messages USING btree ("ConversationId", "CreatedAt");


--
-- Name: IX_messages_SenderId; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_messages_SenderId" ON public.messages USING btree ("SenderId");


--
-- Name: IX_moderation_logs_ActorId_CreatedAt; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_moderation_logs_ActorId_CreatedAt" ON public.moderation_logs USING btree ("ActorId", "CreatedAt");


--
-- Name: IX_moderation_logs_CreatedAt; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_moderation_logs_CreatedAt" ON public.moderation_logs USING btree ("CreatedAt");


--
-- Name: IX_moderation_logs_TargetType_TargetId; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_moderation_logs_TargetType_TargetId" ON public.moderation_logs USING btree ("TargetType", "TargetId");


--
-- Name: IX_outbox_messages_ProcessedAt; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_outbox_messages_ProcessedAt" ON public.outbox_messages USING btree ("ProcessedAt");


--
-- Name: IX_refresh_tokens_TokenHash; Type: INDEX; Schema: public; Owner: genesis
--

CREATE UNIQUE INDEX "IX_refresh_tokens_TokenHash" ON public.refresh_tokens USING btree ("TokenHash");


--
-- Name: IX_refresh_tokens_UserId; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_refresh_tokens_UserId" ON public.refresh_tokens USING btree ("UserId");


--
-- Name: IX_reports_ReporterId_TargetType_TargetId; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_reports_ReporterId_TargetType_TargetId" ON public.reports USING btree ("ReporterId", "TargetType", "TargetId");


--
-- Name: IX_reports_Status_CreatedAt; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_reports_Status_CreatedAt" ON public.reports USING btree ("Status", "CreatedAt");


--
-- Name: IX_reports_TargetType_TargetId; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_reports_TargetType_TargetId" ON public.reports USING btree ("TargetType", "TargetId");


--
-- Name: IX_reviews_AuthorId_ListingId; Type: INDEX; Schema: public; Owner: genesis
--

CREATE UNIQUE INDEX "IX_reviews_AuthorId_ListingId" ON public.reviews USING btree ("AuthorId", "ListingId");


--
-- Name: IX_reviews_ListingId; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_reviews_ListingId" ON public.reviews USING btree ("ListingId");


--
-- Name: IX_reviews_TargetUserId_CreatedAt_Id; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_reviews_TargetUserId_CreatedAt_Id" ON public.reviews USING btree ("TargetUserId", "CreatedAt" DESC, "Id" DESC) WHERE ("IsHidden" = false);


--
-- Name: IX_search_misses_CreatedAt; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_search_misses_CreatedAt" ON public.search_misses USING btree ("CreatedAt");


--
-- Name: IX_search_misses_Query; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_search_misses_Query" ON public.search_misses USING btree ("Query");


--
-- Name: IX_subcategories_Category_Slug; Type: INDEX; Schema: public; Owner: genesis
--

CREATE UNIQUE INDEX "IX_subcategories_Category_Slug" ON public.subcategories USING btree ("Category", "Slug");


--
-- Name: IX_users_Email; Type: INDEX; Schema: public; Owner: genesis
--

CREATE UNIQUE INDEX "IX_users_Email" ON public.users USING btree ("Email");


--
-- Name: IX_verification_codes_UserId_Channel_CreatedAt; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX "IX_verification_codes_UserId_Channel_CreatedAt" ON public.verification_codes USING btree ("UserId", "Channel", "CreatedAt");


--
-- Name: idx_qrtz_ft_inst_job_req_rcvry; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_ft_inst_job_req_rcvry ON public.qrtz_fired_triggers USING btree (sched_name, instance_name, requests_recovery);


--
-- Name: idx_qrtz_ft_j_g; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_ft_j_g ON public.qrtz_fired_triggers USING btree (sched_name, job_name, job_group);


--
-- Name: idx_qrtz_ft_jg; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_ft_jg ON public.qrtz_fired_triggers USING btree (sched_name, job_group);


--
-- Name: idx_qrtz_ft_t_g; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_ft_t_g ON public.qrtz_fired_triggers USING btree (sched_name, trigger_name, trigger_group);


--
-- Name: idx_qrtz_ft_tg; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_ft_tg ON public.qrtz_fired_triggers USING btree (sched_name, trigger_group);


--
-- Name: idx_qrtz_ft_trig_inst_name; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_ft_trig_inst_name ON public.qrtz_fired_triggers USING btree (sched_name, instance_name);


--
-- Name: idx_qrtz_j_grp; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_j_grp ON public.qrtz_job_details USING btree (sched_name, job_group);


--
-- Name: idx_qrtz_j_req_recovery; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_j_req_recovery ON public.qrtz_job_details USING btree (sched_name, requests_recovery);


--
-- Name: idx_qrtz_t_c; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_t_c ON public.qrtz_triggers USING btree (sched_name, calendar_name);


--
-- Name: idx_qrtz_t_g; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_t_g ON public.qrtz_triggers USING btree (sched_name, trigger_group);


--
-- Name: idx_qrtz_t_j; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_t_j ON public.qrtz_triggers USING btree (sched_name, job_name, job_group);


--
-- Name: idx_qrtz_t_jg; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_t_jg ON public.qrtz_triggers USING btree (sched_name, job_group);


--
-- Name: idx_qrtz_t_n_g_state; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_t_n_g_state ON public.qrtz_triggers USING btree (sched_name, trigger_group, trigger_state);


--
-- Name: idx_qrtz_t_n_state; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_t_n_state ON public.qrtz_triggers USING btree (sched_name, trigger_name, trigger_group, trigger_state);


--
-- Name: idx_qrtz_t_next_fire_time; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_t_next_fire_time ON public.qrtz_triggers USING btree (sched_name, next_fire_time);


--
-- Name: idx_qrtz_t_nft_misfire; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_t_nft_misfire ON public.qrtz_triggers USING btree (sched_name, misfire_instr, next_fire_time);


--
-- Name: idx_qrtz_t_nft_st; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_t_nft_st ON public.qrtz_triggers USING btree (sched_name, trigger_state, next_fire_time);


--
-- Name: idx_qrtz_t_nft_st_misfire; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_t_nft_st_misfire ON public.qrtz_triggers USING btree (sched_name, misfire_instr, next_fire_time, trigger_state);


--
-- Name: idx_qrtz_t_nft_st_misfire_grp; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_t_nft_st_misfire_grp ON public.qrtz_triggers USING btree (sched_name, misfire_instr, next_fire_time, trigger_group, trigger_state);


--
-- Name: idx_qrtz_t_state; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX idx_qrtz_t_state ON public.qrtz_triggers USING btree (sched_name, trigger_state);


--
-- Name: ix_outbox_due; Type: INDEX; Schema: public; Owner: genesis
--

CREATE INDEX ix_outbox_due ON public.outbox_messages USING btree ("Status", "NextAttemptAt", "CreatedAt") WHERE (("Status")::text = 'Pending'::text);


--
-- Name: favorites trg_favorites_count; Type: TRIGGER; Schema: public; Owner: genesis
--

CREATE TRIGGER trg_favorites_count AFTER INSERT OR DELETE ON public.favorites FOR EACH ROW EXECUTE FUNCTION public.favorites_count_sync();


--
-- Name: reviews trg_reviews_rating; Type: TRIGGER; Schema: public; Owner: genesis
--

CREATE TRIGGER trg_reviews_rating AFTER INSERT OR DELETE OR UPDATE ON public.reviews FOR EACH ROW EXECUTE FUNCTION public.reviews_rating_sync();


--
-- Name: conversations FK_conversations_listings_ListingId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.conversations
    ADD CONSTRAINT "FK_conversations_listings_ListingId" FOREIGN KEY ("ListingId") REFERENCES public.listings("Id") ON DELETE RESTRICT;


--
-- Name: conversations FK_conversations_users_BuyerId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.conversations
    ADD CONSTRAINT "FK_conversations_users_BuyerId" FOREIGN KEY ("BuyerId") REFERENCES public.users("Id") ON DELETE RESTRICT;


--
-- Name: conversations FK_conversations_users_SellerId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.conversations
    ADD CONSTRAINT "FK_conversations_users_SellerId" FOREIGN KEY ("SellerId") REFERENCES public.users("Id") ON DELETE RESTRICT;


--
-- Name: favorites FK_favorites_listings_ListingId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.favorites
    ADD CONSTRAINT "FK_favorites_listings_ListingId" FOREIGN KEY ("ListingId") REFERENCES public.listings("Id") ON DELETE CASCADE;


--
-- Name: favorites FK_favorites_users_UserId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.favorites
    ADD CONSTRAINT "FK_favorites_users_UserId" FOREIGN KEY ("UserId") REFERENCES public.users("Id") ON DELETE CASCADE;


--
-- Name: listing_images FK_listing_images_listings_ListingId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.listing_images
    ADD CONSTRAINT "FK_listing_images_listings_ListingId" FOREIGN KEY ("ListingId") REFERENCES public.listings("Id") ON DELETE CASCADE;


--
-- Name: listings FK_listings_subcategories_SubcategoryId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.listings
    ADD CONSTRAINT "FK_listings_subcategories_SubcategoryId" FOREIGN KEY ("SubcategoryId") REFERENCES public.subcategories("Id") ON DELETE RESTRICT;


--
-- Name: listings FK_listings_users_OwnerId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.listings
    ADD CONSTRAINT "FK_listings_users_OwnerId" FOREIGN KEY ("OwnerId") REFERENCES public.users("Id") ON DELETE RESTRICT;


--
-- Name: messages FK_messages_conversations_ConversationId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.messages
    ADD CONSTRAINT "FK_messages_conversations_ConversationId" FOREIGN KEY ("ConversationId") REFERENCES public.conversations("Id") ON DELETE CASCADE;


--
-- Name: messages FK_messages_users_SenderId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.messages
    ADD CONSTRAINT "FK_messages_users_SenderId" FOREIGN KEY ("SenderId") REFERENCES public.users("Id") ON DELETE RESTRICT;


--
-- Name: profiles FK_profiles_users_UserId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.profiles
    ADD CONSTRAINT "FK_profiles_users_UserId" FOREIGN KEY ("UserId") REFERENCES public.users("Id") ON DELETE CASCADE;


--
-- Name: refresh_tokens FK_refresh_tokens_users_UserId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.refresh_tokens
    ADD CONSTRAINT "FK_refresh_tokens_users_UserId" FOREIGN KEY ("UserId") REFERENCES public.users("Id") ON DELETE CASCADE;


--
-- Name: reviews FK_reviews_listings_ListingId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.reviews
    ADD CONSTRAINT "FK_reviews_listings_ListingId" FOREIGN KEY ("ListingId") REFERENCES public.listings("Id") ON DELETE RESTRICT;


--
-- Name: reviews FK_reviews_users_AuthorId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.reviews
    ADD CONSTRAINT "FK_reviews_users_AuthorId" FOREIGN KEY ("AuthorId") REFERENCES public.users("Id") ON DELETE RESTRICT;


--
-- Name: reviews FK_reviews_users_TargetUserId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.reviews
    ADD CONSTRAINT "FK_reviews_users_TargetUserId" FOREIGN KEY ("TargetUserId") REFERENCES public.users("Id") ON DELETE RESTRICT;


--
-- Name: verification_codes FK_verification_codes_users_UserId; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.verification_codes
    ADD CONSTRAINT "FK_verification_codes_users_UserId" FOREIGN KEY ("UserId") REFERENCES public.users("Id") ON DELETE CASCADE;


--
-- Name: qrtz_blob_triggers qrtz_blob_triggers_sched_name_trigger_name_trigger_group_fkey; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_blob_triggers
    ADD CONSTRAINT qrtz_blob_triggers_sched_name_trigger_name_trigger_group_fkey FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES public.qrtz_triggers(sched_name, trigger_name, trigger_group) ON DELETE CASCADE;


--
-- Name: qrtz_cron_triggers qrtz_cron_triggers_sched_name_trigger_name_trigger_group_fkey; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_cron_triggers
    ADD CONSTRAINT qrtz_cron_triggers_sched_name_trigger_name_trigger_group_fkey FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES public.qrtz_triggers(sched_name, trigger_name, trigger_group) ON DELETE CASCADE;


--
-- Name: qrtz_simple_triggers qrtz_simple_triggers_sched_name_trigger_name_trigger_group_fkey; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_simple_triggers
    ADD CONSTRAINT qrtz_simple_triggers_sched_name_trigger_name_trigger_group_fkey FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES public.qrtz_triggers(sched_name, trigger_name, trigger_group) ON DELETE CASCADE;


--
-- Name: qrtz_simprop_triggers qrtz_simprop_triggers_sched_name_trigger_name_trigger_grou_fkey; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_simprop_triggers
    ADD CONSTRAINT qrtz_simprop_triggers_sched_name_trigger_name_trigger_grou_fkey FOREIGN KEY (sched_name, trigger_name, trigger_group) REFERENCES public.qrtz_triggers(sched_name, trigger_name, trigger_group) ON DELETE CASCADE;


--
-- Name: qrtz_triggers qrtz_triggers_sched_name_job_name_job_group_fkey; Type: FK CONSTRAINT; Schema: public; Owner: genesis
--

ALTER TABLE ONLY public.qrtz_triggers
    ADD CONSTRAINT qrtz_triggers_sched_name_job_name_job_group_fkey FOREIGN KEY (sched_name, job_name, job_group) REFERENCES public.qrtz_job_details(sched_name, job_name, job_group);


--
-- PostgreSQL database dump complete
--

\unrestrict VlECEttTYO5ieRFviSxiPJZEiNp0qMLYFukBif73fjeNzIerUOgpKMslI6u1pdc

