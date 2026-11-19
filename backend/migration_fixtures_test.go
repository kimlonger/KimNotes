package main

// These fixtures are used by tests that exercise individual migrations in
// temporary schemas. Production and the fixtures read the same embedded SQL.
func migrationFixture(version string) string {
	source, err := migrationFiles.ReadFile("migrations/" + version + ".sql")
	if err != nil {
		panic(err)
	}
	return string(source)
}

var (
	schema                        = migrationFixture("001_initial")
	sessionSchema                 = migrationFixture("002_sessions")
	eventSchema                   = migrationFixture("003_auth_events")
	mainAccountSchema             = migrationFixture("004_main_account")
	contentSchema                 = migrationFixture("005_content_catalog")
	guitarSchema                  = migrationFixture("006_guitar")
	guitarCatalogSchema           = migrationFixture("007_guitar_catalog")
	guitarOriginSchema            = migrationFixture("008_guitar_origin")
	guitarExperienceSchema        = migrationFixture("009_guitar_experience")
	guitarPagesSchema             = migrationFixture("010_guitar_score_pages")
	guitarStarsSchema             = migrationFixture("011_guitar_stars")
	guitarHomeSchema              = migrationFixture("012_guitar_home")
	guitarWorkbenchSchema         = migrationFixture("014_guitar_workbench")
	guitarRequestAnswerSchema     = migrationFixture("015_guitar_request_answers")
	guitarCommunityQuestionSchema = migrationFixture("016_guitar_community_questions")
	guitarEquippedTitleSchema     = migrationFixture("017_guitar_equipped_title")
	guitarScoreAttributesSchema   = migrationFixture("018_guitar_score_attributes")
	guitarArtistPhotosSchema      = migrationFixture("019_guitar_artist_photos")
	guitarSignatureSchema         = migrationFixture("021_guitar_signature")
	guitarRequestCategorySchema   = migrationFixture("022_guitar_request_category")
	guitarMediaIntegritySchema    = migrationFixture("023_guitar_media_integrity")
	accountPointsSchema           = migrationFixture("024_account_points")
	guitarPrivateUploadsSchema    = migrationFixture("025_guitar_private_uploads")
)
