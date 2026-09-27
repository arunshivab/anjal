# SettingRow

**Namespace:** `Anjal.Store`

One stored, non-secret setting of a service.

## Members

- **ServerScope** *(field)* - The server scope.
- **WebmailScope** *(field)* - The webmail scope.
- **Key** *(property)* - The setting's name, for example ANJAL_TLS_DEFAULT_MODE.
- **Scope** *(property)* - Which service the setting belongs to: or .
- **UpdatedAt** *(property)* - When it was last changed.
- **UpdatedBy** *(property)* - Who changed it: "import" for the one-time import, otherwise the API caller.
- **Value** *(property)* - The value.
