#!/usr/bin/env python3
"""Validate that the seed file has the expected number of business rule templates."""
import re
import sys

with open('backend/Migrations/002_SeedData.cs', 'r') as f:
    content = f.read()

# Count distinct rule INSERTs (some migrations use a loop with one INSERT,
# others use one INSERT per rule; both patterns are valid).
inserts = re.findall(r'INSERT INTO business_rules', content)
print(f'   Found {len(inserts)} business_rules INSERT statement(s) in seed file')

# Count unique event names in the rules array.
events = re.findall(r'"([A-Z][A-Za-z]+(?:\w+))",\s*\d+,', content)
unique_events = set(events)
print(f'   Found {len(unique_events)} unique rule event names in seed file')

# Pass if we have at least 6 unique rules defined.
ok = len(unique_events) >= 6
sys.exit(0 if ok else 1)
