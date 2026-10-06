\set ON_ERROR_STOP on
SELECT format('CREATE ROLE scm_production LOGIN PASSWORD %L', :'production_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'scm_production') \gexec
SELECT 'CREATE DATABASE scm_production OWNER scm_production'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'scm_production') \gexec
SELECT 'CREATE DATABASE scm_production_test OWNER scm_production'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'scm_production_test') \gexec
REVOKE CONNECT ON DATABASE scm_procurement, scm_procurement_test, scm_production, scm_production_test FROM PUBLIC;
GRANT CONNECT ON DATABASE scm_procurement, scm_procurement_test TO scm_procurement;
GRANT CONNECT ON DATABASE scm_production, scm_production_test TO scm_production;
