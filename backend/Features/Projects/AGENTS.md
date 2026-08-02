# Projects Feature

## Purpose
- Track projects and their milestones.
- Trigger the `ProjectMilestoneCompleted` event when a milestone is marked done, so the Rules Engine can post the revenue.

## Ownership
- `ProjectModels.cs` — `ProjectDto`, `MilestoneDto`, request records.
- `ProjectService.cs` — CRUD + `CompleteMilestoneAsync` (the only path that dispatches a domain event).
- `ProjectEndpoints.cs` — `GET/POST/PUT/DELETE /api/projects`, milestone sub-routes.

## Local Contracts
- Each project belongs to one company. A project's milestones belong to the project.
- A project has a `status` field: `active`, `completed`, `on_hold`, `cancelled`.
- A milestone has a `status`: `pending`, `completed`. Completing a milestone stamps `completed_at` and adds the amount to the project's `actual_cost`.
- The posting side of completing a milestone is delegated to `RuleEvaluator.TriggerEventAsync` with payload:
  ```json
  {
    "project":    { "id": "...", "name": "...", "nameAr": "..." },
    "milestone":  { "id": "...", "name": "...", "nameAr": "...", "amount": 1000 }
  }
  ```
- The shipped rule template `ProjectMilestoneCompleted` produces:
  - Debit `Accounts Receivable` (1200) for `milestone.amount`
  - Credit `Service Revenue` (4100) for `milestone.amount`

## Work Guidance
- Adding project expense tracking: create a `project_expenses` table linked to `project_id` and post them to the project's cost center.
- Adding time tracking: a `time_entries` table; each entry adds to `actual_cost` on submit.
- Adding project reports: query the `journal_entries` table for entries whose narration contains the project name.

## Verification
- Creating a project with two milestones, then completing one milestone produces exactly one posted journal entry via the rules engine.
- The project's `actual_cost` reflects the sum of completed milestones.
- The income statement shows the milestone revenue once it's been posted and `milestone.amount` lands in the right period.

## Child DOX Index
- *(No child folders; this is a leaf.)*
