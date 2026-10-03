#!/bin/sh
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres -v app_password="$PROCUREMENT_PASSWORD" <<'SQL'
CREATE ROLE scm_procurement LOGIN PASSWORD :'app_password';
CREATE DATABASE scm_procurement OWNER scm_procurement;
CREATE DATABASE scm_procurement_test OWNER scm_procurement;
SQL
