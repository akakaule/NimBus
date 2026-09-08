# CRM/ERP workflow warning cleanup

Address the eleven warning annotations shown on run 34108782186: move its four
GitHub Actions to supported Node 24 releases, enable the Aspire CLI bundle for the
demo AppHost, and correct the displayed nullability warnings in Platform,
EventType.Equals and RoleAssignment.

Keep lazy cache invalidation and equality behavior unchanged. Initialize required
role-assignment strings to empty values so default instances honor the existing
non-null contract without introducing a required-member source break. Do not
suppress compiler warnings or expand this into the repository-wide nullable
migration: the full build log contains many warnings beyond the annotations.
