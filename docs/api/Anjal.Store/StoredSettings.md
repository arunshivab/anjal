# StoredSettings

**Namespace:** `Anjal.Store`

Non-secret settings kept in the database (v1.0.0-rc.7, owner's decision of 27 Sep 2026), so a rebuild from the database backup brings the settings back. Each service calls first thing at start-up: on the first start with an empty table the service's current environment (its /etc/anjal/*.env) is imported once; from then on the stored value of every setting replaces the environment's, and each difference is logged. Secrets - the database connection, passwords, tokens and the KEK - are never stored: they stay in the env files and on the paper custody forms.

## Members

- **ApplyAsync** *(method)* - Apply the stored settings of to this process's environment, importing the environment once if nothing is stored yet. Never throws for an unreachable database or a missing table: the service then runs on its environment, and the log says so.
- **FromEnvironment** *(method)* - The storable settings in an environment, as rows to import.
- **IsSecret** *(method)* - True for a setting that must never be stored in the database: the database connection itself and anything holding a password, token, secret or key-encryption key.
- **IsStorable** *(method)* - True for a name that may be stored: an ANJAL_ setting that is not secret.
