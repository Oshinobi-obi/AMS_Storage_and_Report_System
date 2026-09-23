-- Create user_activities table for tracking user actions
-- Run this migration on your MySQL database

CREATE TABLE IF NOT EXISTS user_activities (
    activity_id INT AUTO_INCREMENT PRIMARY KEY,
    user_id INT UNSIGNED NOT NULL,
    activity_type VARCHAR(50) NOT NULL,
    description TEXT NOT NULL,
    ip_address VARCHAR(45) NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (user_id) REFERENCES users(user_id) ON DELETE CASCADE,
    INDEX idx_user_activities_user_id (user_id),
    INDEX idx_user_activities_created_at (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
