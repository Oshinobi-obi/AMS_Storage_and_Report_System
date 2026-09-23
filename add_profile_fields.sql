-- Add profile fields to users table
-- Run this migration on your MySQL database

ALTER TABLE users
ADD COLUMN display_name VARCHAR(255) NOT NULL DEFAULT '' AFTER full_name,
ADD COLUMN profile_picture_path VARCHAR(500) NULL AFTER display_name;

-- Optional: Initialize display_name with full_name for existing users
UPDATE users
SET display_name = full_name
WHERE display_name = '';
