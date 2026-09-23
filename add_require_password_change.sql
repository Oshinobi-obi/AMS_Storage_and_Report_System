-- Add require_password_change column to users table
-- Run this migration on your MySQL database

ALTER TABLE users
ADD COLUMN require_password_change TINYINT(1) NOT NULL DEFAULT 0 AFTER is_active;

-- Optional: Set existing users to not require password change
UPDATE users
SET require_password_change = 0
WHERE require_password_change IS NULL;
