-- Example table. Use your own table/column names in the app's Settings.
CREATE TABLE MY_TABLE (
  NAME1        VARCHAR2(100),            -- value after  A:   (activate)
  NAME2        VARCHAR2(100),            -- value after  D:   (deactivate)
  ADD_USER     VARCHAR2(200),            -- sender e-mail (or fixed text from Settings)
  ADD_DATE     DATE DEFAULT SYSDATE,     -- insert time
  DELETE_USER  VARCHAR2(200),            -- filled when the same A:/D: pair is sent again
  DELETE_DATE  DATE                      -- NULL = row is active
);

-- Already have the table? Just add the two new columns:
-- ALTER TABLE MY_TABLE ADD (DELETE_USER VARCHAR2(200), DELETE_DATE DATE);
