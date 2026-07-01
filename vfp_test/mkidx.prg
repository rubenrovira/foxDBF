SET NOTIFY OFF
SET TALK OFF
SET SAFETY OFF
SET EXCLUSIVE ON
ON ERROR DO errlog WITH ERROR(), MESSAGE(), PROGRAM(), LINENO()
TRY
   LOCAL lcDir
   lcDir = "C:\Users\bernhard.reiter\Downloads\rubydbf\vfp_test\"
   IF FILE(lcDir + "idxtest.dbf")
      DELETE FILE (lcDir + "idxtest.dbf")
      IF FILE(lcDir + "idxtest.cdx")
         DELETE FILE (lcDir + "idxtest.cdx")
      ENDIF
   ENDIF
   CREATE TABLE (lcDir + "idxtest.dbf") FREE (tid I, tname C(20), tamount N(10,2), tdate D, tdt T)
   INSERT INTO idxtest VALUES (3, "Charlie", 30.50, {^2020-03-03}, {^2020-03-03 10:00:00})
   INSERT INTO idxtest VALUES (1, "alpha",   10.00, {^2019-01-01}, {^2019-01-01 08:30:00})
   INSERT INTO idxtest VALUES (2, "Bravo",   -5.25, {^2021-12-31}, {^2021-12-31 23:59:59})
   INSERT INTO idxtest VALUES (5, "echo",    99.99, {^2018-06-15}, {^2018-06-15 12:00:00})
   INSERT INTO idxtest VALUES (4, "Delta",    0.00, {^2022-02-02}, {^2022-02-02 00:00:01})
   INSERT INTO idxtest VALUES (6, "foxtrot", 12.34, {^2017-11-30}, {^2017-11-30 06:45:30})
   INDEX ON tid     TAG tid
   INDEX ON tname   TAG tname
   INDEX ON tamount TAG tamount
   INDEX ON tdate   TAG tdate
   INDEX ON tdt     TAG tdt
   INDEX ON tname   TAG tnamed DESCENDING
   USE
   STRTOFILE("DONE", "C:\Users\bernhard.reiter\Downloads\rubydbf\vfp_test\mkidx_done.txt", 0)
CATCH TO loErr
   STRTOFILE(TRANSFORM(loErr.ErrorNo) + ": " + loErr.Message + " @" + TRANSFORM(loErr.LineNo), ;
             "C:\Users\bernhard.reiter\Downloads\rubydbf\vfp_test\mkidx_err.log", 0)
ENDTRY
QUIT

PROCEDURE errlog
   LPARAMETERS tnErr, tcMsg, tcPrg, tnLine
   STRTOFILE(TRANSFORM(tnErr) + ": " + tcMsg + " in " + tcPrg + " @" + TRANSFORM(tnLine), ;
             "C:\Users\bernhard.reiter\Downloads\rubydbf\vfp_test\mkidx_err.log", 0)
   QUIT
