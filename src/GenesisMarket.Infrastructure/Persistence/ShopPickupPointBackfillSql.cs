namespace GenesisMarket.Infrastructure.Persistence;

/// <summary>Legacy PickupAddress → primary pickup point; safe to rerun after schema creation.</summary>
public static class ShopPickupPointBackfillSql
{
    public const string Sql = """
        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM business_profiles bp
                LEFT JOIN profiles p ON p."UserId" = bp."UserId"
                WHERE NULLIF(btrim(bp."PickupAddress"), '') IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM shop_pickup_points existing WHERE existing."OwnerId" = bp."UserId"
                  )
                  AND COALESCE(bp."ShopCity", p."City") IS NULL
            ) THEN
                RAISE EXCEPTION 'Cannot migrate pickup address: ShopCity and Profile.City are both missing.';
            END IF;
        END $$;

        INSERT INTO shop_pickup_points
            ("Id", "OwnerId", "City", "Address", "Note", "IsMain", "IsActive", "SortOrder", "Hours", "CreatedAt", "UpdatedAt")
        SELECT gen_random_uuid(), bp."UserId", COALESCE(bp."ShopCity", p."City"), bp."PickupAddress",
               NULL, TRUE, TRUE, 0, NULL, now(), now()
        FROM business_profiles bp
        LEFT JOIN profiles p ON p."UserId" = bp."UserId"
        WHERE NULLIF(btrim(bp."PickupAddress"), '') IS NOT NULL
          AND COALESCE(bp."ShopCity", p."City") IS NOT NULL
          AND NOT EXISTS (
              SELECT 1 FROM shop_pickup_points existing WHERE existing."OwnerId" = bp."UserId"
          )
        ON CONFLICT DO NOTHING;
        """;
}
