-- WT-646: give the plugin catalog the columns the admin portal needs to curate it.
--
-- WHY
--   Until 20260907100000 the catalog held one row, so "how the catalog is presented" was not a
--   question anyone had to answer -- there was nothing to order, group or promote. Splitting
--   google_workspace into three rows, on top of the kind='mcp' rows that 20260828100000 made
--   possible to add by INSERT, means the catalog is now a list that grows without a deploy. A list
--   that grows without a deploy needs its presentation to be data too, otherwise the ordering ends
--   up hardcoded in the frontend and every new app needs a frontend change to appear in the right
--   place.
--
--   All four are additive and nullable-or-defaulted, so every existing row keeps working unchanged
--   and the API can ignore them until the admin portal ships.

ALTER TABLE assistant.plugins
    -- Promotes a row into whatever "featured" strip the catalog page renders. Deliberately not a
    -- unique or limited flag: how many featured rows the page can show is a layout decision, and
    -- pinning it here would make the layout un-changeable without a migration.
    ADD COLUMN IF NOT EXISTS is_featured BOOLEAN NOT NULL DEFAULT false,
    -- Explicit ordering within a listing. Every existing row defaults to 0, which means the
    -- catalog keeps whatever secondary ordering the query already applies until an operator
    -- actually curates it -- adding this column does not silently reshuffle the page.
    ADD COLUMN IF NOT EXISTS sort_order INT NOT NULL DEFAULT 0,
    -- Grouping label for the catalog page ('productivity', 'meetings', ...). Left free-text and
    -- NULL-able rather than a CHECK or an enum: the taxonomy is the least settled thing here, and
    -- a constraint would turn every rename into a migration. NULL means uncategorised, which the
    -- listing renders under a catch-all rather than hiding.
    ADD COLUMN IF NOT EXISTS category VARCHAR(50) NULL,
    -- Which admin last edited the row, for attribution in the portal's audit trail. No FK: users
    -- live in AuthService's own logical database (warptalk_auth), and a cross-database reference
    -- is not something Postgres can enforce. NULL means the row was written by a migration or a
    -- seed rather than by a person, which is true of every row that exists today.
    ADD COLUMN IF NOT EXISTS updated_by UUID NULL;
