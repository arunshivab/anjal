# PostgresMessageStore

**Namespace:** `Anjal.Store`

PostgreSQL-backed implementation of . Uses Npgsql for connection management; each method opens and closes its own connection through the driver's connection pool. Connection-string is supplied at construction time.

## Members

- **#ctor** *(method)* - Create the store with a PostgreSQL connection string.
- **AddMailboxUsageAsync** *(method)* - _(no description)_
- **CountMessagesAsync** *(method)* - _(no description)_
- **CountOutboundAsync** *(method)* - _(no description)_
- **CreateTagGrantAsync** *(method)* - _(no description)_
- **DeleteDkimKeyAsync** *(method)* - _(no description)_
- **DeleteLocalDomainAsync** *(method)* - _(no description)_
- **DeleteMailboxAsync** *(method)* - _(no description)_
- **DeleteMessageAsync** *(method)* - _(no description)_
- **DeleteOutboundTlsPolicyAsync** *(method)* - _(no description)_
- **DeleteRoutingRuleAsync** *(method)* - _(no description)_
- **DeleteSenderRuleAsync** *(method)* - _(no description)_
- **DeleteSmtpUserAsync** *(method)* - _(no description)_
- **DeleteTenantAsync** *(method)* - _(no description)_
- **DeleteTenantDomainAsync** *(method)* - _(no description)_
- **EnqueueOutboundAsync** *(method)* - _(no description)_
- **EnsureFolderAsync** *(method)* - _(no description)_
- **GetActiveTagGrantAsync** *(method)* - _(no description)_
- **GetDkimKeyAsync** *(method)* - _(no description)_
- **GetInboundByIdAsync** *(method)* - _(no description)_
- **GetMailboxAsync** *(method)* - _(no description)_
- **GetMailboxByIdAsync** *(method)* - _(no description)_
- **GetMessageByIdAsync** *(method)* - _(no description)_
- **GetOutboundByIdAsync** *(method)* - _(no description)_
- **GetOutboundTlsPolicyAsync** *(method)* - _(no description)_
- **GetRoutingRuleAsync** *(method)* - _(no description)_
- **GetSmtpUserAsync** *(method)* - _(no description)_
- **GetTenantAsync** *(method)* - _(no description)_
- **GetTenantByIdAsync** *(method)* - _(no description)_
- **GetTenantDomainAsync** *(method)* - _(no description)_
- **IsLocalDomainAsync** *(method)* - _(no description)_
- **LeaseOutboundBatchAsync** *(method)* - _(no description)_
- **ListDkimKeysAsync** *(method)* - _(no description)_
- **ListFoldersAsync** *(method)* - _(no description)_
- **ListLocalDomainsAsync** *(method)* - _(no description)_
- **ListMailboxesAsync** *(method)* - _(no description)_
- **ListMessagesAsync** *(method)* - _(no description)_
- **ListOutboundTlsPoliciesAsync** *(method)* - _(no description)_
- **ListRoutingRulesAsync** *(method)* - _(no description)_
- **ListSenderRulesAsync** *(method)* - _(no description)_
- **ListSmtpUsersAsync** *(method)* - _(no description)_
- **ListTenantDomainsAsync** *(method)* - _(no description)_
- **ListTenantsAsync** *(method)* - _(no description)_
- **MarkOutboundResultAsync** *(method)* - _(no description)_
- **MoveMessageAsync** *(method)* - _(no description)_
- **SaveInboundMessageAsync** *(method)* - _(no description)_
- **SaveMessageAsync** *(method)* - _(no description)_
- **SaveWebhookDeliveryAsync** *(method)* - _(no description)_
- **SetMessageFlagsAsync** *(method)* - _(no description)_
- **UpsertDkimKeyAsync** *(method)* - _(no description)_
- **UpsertLocalDomainAsync** *(method)* - _(no description)_
- **UpsertMailboxAsync** *(method)* - _(no description)_
- **UpsertOutboundTlsPolicyAsync** *(method)* - _(no description)_
- **UpsertRoutingRuleAsync** *(method)* - _(no description)_
- **UpsertSenderRuleAsync** *(method)* - _(no description)_
- **UpsertSmtpUserAsync** *(method)* - _(no description)_
- **UpsertTenantAsync** *(method)* - _(no description)_
- **UpsertTenantDomainAsync** *(method)* - _(no description)_
