INSERT INTO billing."SubscriptionPlans" (
	"Id",
	"Type",
	"MonthlyPriceVnd",
	"IncludedCredits",
	"VoiceTranslationRatePerHour",
	"TextTranslationRatePerHour",
	"VoiceCloningMultiplier",
	"MultiLanguageStreamMultiplier",
	"AiAssistantMultiplier",
	"MaxParticipants",
	"MaxConcurrentMeetings",
	"MaxLanguagesPerMeeting",
	"SupportsVoiceCloning",
	"SupportsAiAssistant",
	"SupportsEnterpriseGlossary",
	"SupportsMultiLanguageRoom",
	"SupportsCreditRollover",
	"IsActive",
	"CreatedAt",
	"UpdatedAt"
) VALUES
	('11111111-1111-1111-1111-111111111111', 'Free', 0, 30, 30, 10, 1, 1, 1, 5, 1, 1, FALSE, FALSE, FALSE, FALSE, FALSE, TRUE, '2026-01-01T00:00:00Z', NULL),
	('22222222-2222-2222-2222-222222222222', 'Pro', 199000, 500, 12, 4, 1.5, 1.2, 1.2, 25, 5, 2, TRUE, TRUE, FALSE, TRUE, FALSE, TRUE, '2026-01-01T00:00:00Z', NULL),
	('33333333-3333-3333-3333-333333333333', 'Premium', 499000, 1000, 10, 3, 1.3, 1.1, 1.1, 100, 15, 4, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, '2026-01-01T00:00:00Z', NULL),
	('44444444-4444-4444-4444-444444444444', 'Enterprise', 0, 10000, 8, 2, 1, 1, 1, 1000, 100, 10, TRUE, TRUE, TRUE, TRUE, TRUE, TRUE, '2026-01-01T00:00:00Z', NULL)
ON CONFLICT ("Type") DO NOTHING;
