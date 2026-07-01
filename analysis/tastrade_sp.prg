#INCLUDE TASTRADE.H

FUNCTION NewID(tcAlias)
  LOCAL lcAlias, ;
        lcID, ;
        lcOldReprocess, ;
        lnOldArea

  lnOldArea = SELECT()
  
  IF PARAMETERS() < 1
    lcAlias = UPPER(ALIAS())
  ELSE
    lcAlias = UPPER(tcAlias)
  ENDIF
        
  lcID = ""
  lcOldReprocess = SET('REPROCESS')
  
  *-- Lock until user presses Esc
  SET REPROCESS TO AUTOMATIC

  IF !USED("SETUP")
    USE tastrade!setup IN 0
  ENDIF
  SELECT setup
      
  IF SEEK(lcAlias, "setup", "key_name")
    IF RLOCK()
      lcID = setup.value
      REPLACE setup.value WITH ;
              STR(VAL(ALLT(lcID)) + 1, LEN(setup.value))
      UNLOCK
    ENDIF
  ENDIF
    
  SELECT (lnOldArea)
  SET REPROCESS TO lcOldReprocess
  
  RETURN lcID
ENDFUNC

******************     
FUNCTION RemainingCredit(tcCustomerID)
  LOCAL lyMaxOrderAmount, ;
        lyTotalOrders, ;
        lcCustomerAlias, ;
        lnOldArea
        
  IF PARAMETERS() < 1 OR EMPTY(tcCustomerID)
    RETURN 0
  ENDIF
  
  lyMaxOrderAmount = 0
  
  lnOldArea = SELECT(0)
  lcCustomerAlias = SYS(2015)
  USE tastrade!customer AGAIN IN 0 ALIAS (lcCustomerAlias)
  IF SEEK(tcCustomerID, lcCustomerAlias, "customer_i")
    lyMaxOrderAmount = EVAL(lcCustomerAlias + ".max_order_amt")
  ENDIF
  USE IN (lcCustomerAlias)
  
  *-- We use the orders table again to avoid "Illegal 
  *-- recursion" errors.
  USE tastrade!orders AGAIN ALIAS _orders IN 0

  SELECT SUM((b.unit_price * b.quantity) - ;
      (a.discount * .01) * (b.unit_price * b.quantity) + a.freight) AS totalorder, a.order_id ;
    FROM _orders a, order_line_items b ;
   WHERE a.order_id = b.order_id AND ;
     a.customer_id = tcCustomerID AND ;
     !a.paid ;
   GROUP by a.order_id ;
    INTO CURSOR orderamounts

  SUM totalorder to lyTotalOrders

  USE IN _orders
  USE IN orderamounts

  SELECT (lnOldArea)
  
  RETURN lyMaxOrderAmount - lyTotalOrders
ENDFUNC   
       
******************
FUNCTION ValOrder()
  LOCAL llRetVal, ;
        lnAnswer, ;
        llClose, ;
        lnOldRecNo, ;
        lnOrderTotal, ;
        lnOldArea, ;
        lcCustomerAlias, ;
        lyMinOrdAmount, ;
        lyOrderAmount  
        
  *-- Don't bother checking if we're deleting the order
  IF DELETED()
    RETURN
  ENDIF

  lnOldArea = SELECT()
    
  *-- Check if we have at least one line item. Use the current
  *-- table if order_line_items is already opened to include
  *-- buffered records.
  IF !USED("order_line_items")
    USE tastrade!order_line_items AGAIN IN 0
    llClose = .T.
  ENDIF

  lnOldRecNo = IIF(RECCOUNT("order_line_items") > 0, RECNO("order_line_items"), 0)
  llRetVal = (SEEK(orders.order_id, "order_line_items", "order_id") AND ;
          !EMPTY(order_line_items.product_id))
  IF lnOldRecNo <> 0 AND llRetVal
    GO lnOldRecNo IN order_line_items
  ENDIF
  
  IF !llRetVal
    =MESSAGEBOX(ORDHASITEMS_LOC, ;
                MB_ICONEXCLAMATION, ;
                TASTRADE_LOC)
  ENDIF
  
  lyOrderAmount = CalcMinOrdAmount(orders.order_id)
  IF llRetVal
    *-- Check if customer is over credit limit. If 
    *-- customer id is empty, the order will not be saved
    *-- because customer_id is a required foreign key, enforced
    *-- by the RI triggers, so we don't even bother checking.
    IF !EMPTY(orders.customer_id)
      IF !(GETFLDSTATE("paid","orders") == 2 AND OCCURS("2",GETFLDSTATE(-1,"Orders")) == 1) AND ;
      	LOWER(_screen.ActiveForm.Name) # "frmordhistory"
        *- didn't just change paid, and isn't on order history screen
        LOCAL lyRemainingCredit
        lyRemainingCredit = RemainingCredit(orders.customer_id) + IIF(orders.paid, lyOrderAmount, 0)
        IF lyRemainingCredit < $0
          lnAnswer = ;
            MESSAGEBOX(CUSTOVERMAX_LOC + ;
                        DOLLAR_FORMAT3_LOC + ALLT(STR(ABS(lyRemainingCredit), 15, 2)) + DOLLAR_FORMAT2_LOC + CR + ;
                        SAVEANYWAY_LOC, ;
                     MB_ICONQUESTION + MB_YESNO, ;
                     TASTRADE_LOC)
          llRetVal = (lnAnswer = IDYES)
        ENDIF
      ENDIF
      
      IF llRetVal
        *-- Get the customer's minimum order amount
        IF !(GETFLDSTATE("paid","orders") == 2 AND OCCURS("2",GETFLDSTATE(-1,"Orders")) == 1) AND ;
   		  LOWER(_screen.ActiveForm.Name) # "frmordhistory"
          lcCustomerAlias = SYS(2015)
          USE tastrade!customer AGAIN IN 0 ALIAS (lcCustomerAlias)
          IF SEEK(orders.customer_id, lcCustomerAlias, "customer_i")
            lyMinOrdAmount = EVAL(lcCustomerAlias + ".min_order_amt")
          ENDIF
          USE IN (lcCustomerAlias)

          IF lyOrderAmount < lyMinOrdAmount
            lnAnswer = ;
              MESSAGEBOX(CUSTUNDERMIN_LOC + ;
                         DOLLAR_FORMAT3_LOC + ALLT(STR(lyMinOrdAmount, 15, 2)) + DOLLAR_FORMAT2_LOC + CR + ;
                         SAVEANYWAY_LOC, ;
                       MB_ICONQUESTION + MB_YESNO, ;
                       TASTRADE_LOC)
            llRetVal = (lnAnswer = IDYES)
          ENDIF
        ENDIF
      ENDIF
    ENDIF	&& marked as paid only
  ENDIF
 
  IF llClose
    USE IN order_line_items
  ENDIF
  
  SELECT (lnOldArea)
  
  RETURN llRetVal
ENDFUNC   

******************
FUNCTION CalcMinOrdAmount(tcOrderID)
  *-- Returns order amount
  *-- Assumes orders table is open and positioned on desired record
  LOCAL lyOrderTotal, ;
        llClose
          
  IF !USED("order_line_items")
    USE tastrade!order_line_items IN 0
    llClose = .T.
  ENDIF
  SELECT order_line_items

  lnOldRecNo = IIF(EOF(), 0, RECNO())
  SUM (unit_price * quantity) - ;
    (orders.discount * .01) * (unit_price * quantity) ;
    FOR order_id = tcOrderID ;
    TO lyOrderTotal 

  IF lnOldRecNo <> 0
    GO lnOldRecNo IN order_line_items
  ENDIF

  IF llClose
    USE IN order_line_items
  ENDIF
  
  RETURN lyOrderTotal 

ENDFUNC

******************
FUNCTION CalcOrdTotal(tcOrderID)
  *-- Returns total of order, after discount
  *-- Assumes orders table is open and positioned on desired record
  LOCAL lyOrderTotal, ;
        llClose,;
        liSelect
          
  IF !USED("order_line_items")
    USE tastrade!order_line_items IN 0
    llClose = .T.
  ENDIF
  liSelect = SELECT()
  SELECT order_line_items

  lnOldRecNo = IIF(EOF(), 0, RECNO())
  SUM (unit_price * quantity) - ;
    (orders.discount * .01) * (unit_price * quantity) ;
    FOR order_id = tcOrderID ;
    TO lyOrderTotal 

  IF lnOldRecNo <> 0
    GO lnOldRecNo IN order_line_items
  ENDIF

  IF llClose
    USE IN order_line_items
  ENDIF
  
  SELECT (m.liSelect)
  RETURN lyOrderTotal 

ENDFUNC

******************
FUNCTION DefaultEmployee()
  LOCAL lcEmployeeID
  *-- An order must have an employee ID associated with it
  *-- For the purposes of this sample application, we will 
  *-- attempt to use the employee ID for the currently logged in
  *-- employee. If the oApp object does not exist (i.e., we are not
  *-- running the application), we will attempt to retrieve an arbitrary
  *-- employee ID from the employee file
  lcEmployeeID = ""
  IF TYPE("oApp") == "O"
    lcEmployeeID = oApp.GetEmployeeID()
  ENDIF
  IF EMPTY(lcEmployeeID)
    USE tastrade!employee AGAIN IN 0 ALIAS emp
    lcEmployeeID = emp.employee_id
    USE IN emp
  ENDIF
  
  RETURN lcEmployeeID
ENDFUNC


*---------------------------------------------------------------------

**__RI_HEADER!@ Do NOT REMOVE or MODIFY this line!!!! @!__RI_HEADER**
procedure RIDELETE
local llRetVal
llRetVal=.t.
 IF (ISRLOCKED() and !deleted()) OR !RLOCK()
    llRetVal=.F.
  ELSE
    IF !deleted()
      DELETE
      llRetVal=pnerror=0
    ENDIF not already deleted
  ENDIF
  UNLOCK RECORD (RECNO())
RETURN llRetVal

procedure RIUPDATE
lparameters tcFieldName,tcNewValue,tcCascadeParent
local llRetVal
llRetVal=.t.
 IF ISRLOCKED() OR !RLOCK()
    llRetVal=.F.
  ELSE
    IF EVAL(tcFieldName)<>tcNewValue
      PRIVATE pcCascadeParent
      pcCascadeParent=upper(iif(type("tcCascadeParent")<>"C","",tcCascadeParent))
      REPLACE (tcFieldName) WITH tcNewValue
      llRetVal=pnerror=0
    ENDIF values don't already match
  ENDIF it's locked already, or I was able to lock it
  UNLOCK RECORD (RECNO())
return llRetVal

procedure rierror
parameters tnErrNo,tcMessage,tcCode,tcProgram
local lnErrorRows,lnXX
lnErrorRows=alen(gaErrors,1)
if type('gaErrors[lnErrorRows,1]')<>"L"
  dimension gaErrors[lnErrorRows+1,alen(gaErrors,2)]
  lnErrorRows=lnErrorRows+1
endif
gaErrors[lnErrorRows,1]=tnErrNo
gaErrors[lnErrorRows,2]=tcMessage
gaErrors[lnErrorRows,3]=tcCode
gaErrors[lnErrorRows,4]=""
lnXX=1
do while !empty(program(lnXX))
  gaErrors[lnErrorRows,4]=gaErrors[lnErrorRows,4]+","+;
  program(lnXX)
  lnXX=lnXX+1
enddo
gaErrors[lnErrorRows,5]=pcParentDBF
gaErrors[lnErrorRows,6]=pnParentRec
gaErrors[lnErrorRows,7]=pcParentID
gaErrors[lnErrorRows,8]=pcParentExpr
gaErrors[lnErrorRows,9]=pcChildDBF
gaErrors[lnErrorRows,10]=pnChildRec
gaErrors[lnErrorRows,11]=pcChildID
gaErrors[lnErrorRows,12]=pcChildExpr
return tnErrNo


PROCEDURE riopen
PARAMETERS tcTable,tcOrder
local lcCurWkArea,lcNewWkArea,lnInUseSpot
lnInUseSpot=atc(tcTable+"*",pcRIcursors)
IF lnInUseSpot=0
  lcCurWkArea=select()
  SELECT 0
  lcNewWkArea=select()
  IF NOT EMPTY(tcOrder)
    USE (tcTable) AGAIN ORDER (tcOrder) ;
      ALIAS ("__ri"+LTRIM(STR(SELECT()))) share
  ELSE
    USE (tcTable) AGAIN ALIAS ("__ri"+LTRIM(STR(SELECT()))) share
  ENDIF
  if pnerror=0
    pcRIcursors=pcRIcursors+upper(tcTable)+"?"+STR(SELECT(),5)
  else
    lcNewWkArea=0
  endif something bad happened while attempting to open the file
ELSE
  lcNewWkArea=val(substr(pcRIcursors,lnInUseSpot+len(tcTable)+1,5))
  pcRIcursors = strtran(pcRIcursors,upper(tcTable)+"*"+str(lcNewWkArea,5),;
    upper(tcTable)+"?"+str(lcNewWkArea,5))
  IF NOT EMPTY(tcOrder)
    SET ORDER TO (tcOrder) IN (lcNewWkArea)
  ENDIF sent an order
  if pnerror<>0
    lcNewWkArea=0
  endif something bad happened while setting order
ENDIF
RETURN (lcNewWkArea)


PROCEDURE riend
PARAMETERS tlSuccess
local lnXX,lnSpot,lcWorkArea
IF tlSuccess
  END TRANSACTION
ELSE
  SET DELETED OFF
  ROLLBACK
  SET DELETED ON
ENDIF
IF EMPTY(pcRIolderror)
  ON ERROR
ELSE
  ON ERROR &pcRIolderror.
ENDIF
FOR lnXX=1 TO occurs("*",pcRIcursors)
  lnSpot=atc("*",pcRIcursors,lnXX)+1
  USE IN (VAL(substr(pcRIcursors,lnSpot,5)))
ENDFOR
IF pcOldCompat = "ON"
	SET COMPATIBLE ON
ENDIF
IF pcOldDele="OFF"
  SET DELETED OFF
ENDIF
IF pcOldExact="ON"
  SET EXACT ON
ENDIF
IF pcOldTalk="ON"
  SET TALK ON
ENDIF
do case
  case empty(pcOldDBC)
    set data to
  case pcOldDBC<>DBC()
    set data to (pcOldDBC)
endcase
RETURN .T.


PROCEDURE rireuse
* rireuse.prg
PARAMETERS tcTableName,tcWkArea
pcRIcursors = strtran(pcRIcursors,upper(tcTableName)+"?"+str(tcWkArea,5),;
  upper(tcTableName)+"*"+str(tcWkArea,5))
RETURN .t.

********************************************************************************
** "Referential integrity delete trigger for" category
PROCEDURE __RI_DELETE_category
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcParentWkArea
LOCAL llDelHeaderarea
lcStartArea=select()
llRetVal=.t.
lcParentWkArea=select()
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
STORE CATEGORY_ID TO lcParentID,pcParentID
pcParentExpr="CATEGORY_ID"
lcChildWkArea=riopen("products","category_i")
IF lcChildWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  RETURN .F.
ENDIF not able to open the child work area
pcChildDBF=dbf(lcChildWkArea)
llRetVal=!SEEK(lcParentID,lcChildWkArea)
SELECT (lcChildWkArea)
pnChildRec=recno()
pcChildID=CATEGORY_ID
pcChildExpr="CATEGORY_ID"
IF !llRetVal
  DO rierror with -1,"Delete restrict rule violated.","",""
ENDIF
=rireuse("products",lcChildWkArea)
IF NOT llRetVal
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Delete trigger for" category
********************************************************************************

********************************************************************************
procedure __RI_UPDATE_category
** "Referential integrity update trigger for" category
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcOldParentID && previous parent id value
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcChildID && child's value to be sought in parent
LOCAL lcOldChildID && old child id value
LOCAL lcParentWkArea && parentwork area handle returned by riopen
LOCAL lcStartArea
lcStartArea=select()
llRetVal=.t.
lcParentWkArea=select()
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
lcOldParentID=OLDVAL("CATEGORY_ID")
pcParentID=lcOldParentID
pcParentExpr="CATEGORY_ID"
lcParentID=CATEGORY_ID
IF lcParentID<>lcOldParentID
  lcChildWkArea=riopen("products")
  IF lcChildWkArea<=0
    IF _triggerlevel=1
      DO riend WITH .F.
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN .F.
  ENDIF not able to open the child work area
  pcChildDBF=dbf(lcChildWkArea)
  SELECT (lcChildWkArea)
  SCAN FOR CATEGORY_ID=lcOldParentID
    pnChildRec=recno()
    pcChildID=CATEGORY_ID
    pcChildExpr="CATEGORY_ID"
    IF NOT llRetVal
      EXIT
    ENDIF && not llretval
    llRetVal=riupdate("CATEGORY_ID",lcParentID,"CATEGORY")
  ENDSCAN get all of the products records
  =rireuse("products",lcChildWkArea)
  IF NOT llRetVal
    IF _triggerlevel=1
      DO riend WITH llRetVal
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN llRetVal
  ENDIF
ENDIF this parent id changed
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Update trigger for" category
********************************************************************************

********************************************************************************
** "Referential integrity delete trigger for" customer
PROCEDURE __RI_DELETE_customer
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcParentWkArea
LOCAL llDelHeaderarea
lcStartArea=select()
llRetVal=.t.
lcParentWkArea=select()
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
STORE CUSTOMER_ID TO lcParentID,pcParentID
pcParentExpr="CUSTOMER_ID"
lcChildWkArea=riopen("orders","customer_i")
IF lcChildWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  RETURN .F.
ENDIF not able to open the child work area
pcChildDBF=dbf(lcChildWkArea)
llRetVal=!SEEK(lcParentID,lcChildWkArea)
SELECT (lcChildWkArea)
pnChildRec=recno()
pcChildID=CUSTOMER_ID
pcChildExpr="CUSTOMER_ID"
IF !llRetVal
  DO rierror with -1,"Delete restrict rule violated.","",""
ENDIF
=rireuse("orders",lcChildWkArea)
IF NOT llRetVal
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Delete trigger for" customer
********************************************************************************

********************************************************************************
procedure __RI_UPDATE_customer
** "Referential integrity update trigger for" customer
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcOldParentID && previous parent id value
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcChildID && child's value to be sought in parent
LOCAL lcOldChildID && old child id value
LOCAL lcParentWkArea && parentwork area handle returned by riopen
LOCAL lcStartArea
lcStartArea=select()
llRetVal=.t.
lcParentWkArea=select()
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
lcOldParentID=OLDVAL("CUSTOMER_ID")
pcParentID=lcOldParentID
pcParentExpr="CUSTOMER_ID"
lcParentID=CUSTOMER_ID
IF lcParentID<>lcOldParentID
  lcChildWkArea=riopen("orders")
  IF lcChildWkArea<=0
    IF _triggerlevel=1
      DO riend WITH .F.
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN .F.
  ENDIF not able to open the child work area
  pcChildDBF=dbf(lcChildWkArea)
  SELECT (lcChildWkArea)
  SCAN FOR CUSTOMER_ID=lcOldParentID
    pnChildRec=recno()
    pcChildID=CUSTOMER_ID
    pcChildExpr="CUSTOMER_ID"
    IF NOT llRetVal
      EXIT
    ENDIF && not llretval
    llRetVal=riupdate("CUSTOMER_ID",lcParentID,"CUSTOMER")
  ENDSCAN get all of the orders records
  =rireuse("orders",lcChildWkArea)
  IF NOT llRetVal
    IF _triggerlevel=1
      DO riend WITH llRetVal
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN llRetVal
  ENDIF
ENDIF this parent id changed
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Update trigger for" customer
********************************************************************************

********************************************************************************
** "Referential integrity delete trigger for" employee
PROCEDURE __RI_DELETE_employee
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcParentWkArea
LOCAL llDelHeaderarea
lcStartArea=select()
llRetVal=.t.
lcParentWkArea=select()
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
STORE EMPLOYEE_ID TO lcParentID,pcParentID
pcParentExpr="EMPLOYEE_ID"
lcChildWkArea=riopen("orders","employee_i")
IF lcChildWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  RETURN .F.
ENDIF not able to open the child work area
pcChildDBF=dbf(lcChildWkArea)
llRetVal=!SEEK(lcParentID,lcChildWkArea)
SELECT (lcChildWkArea)
pnChildRec=recno()
pcChildID=EMPLOYEE_ID
pcChildExpr="EMPLOYEE_ID"
IF !llRetVal
  DO rierror with -1,"Delete restrict rule violated.","",""
ENDIF
=rireuse("orders",lcChildWkArea)
IF NOT llRetVal
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Delete trigger for" employee
********************************************************************************

********************************************************************************
procedure __RI_UPDATE_employee
** "Referential integrity update trigger for" employee
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcOldParentID && previous parent id value
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcChildID && child's value to be sought in parent
LOCAL lcOldChildID && old child id value
LOCAL lcParentWkArea && parentwork area handle returned by riopen
LOCAL lcStartArea
lcStartArea=select()
llRetVal=.t.
lcChildWkArea=select()
IF _triggerlevel=1 or type("pccascadeparent")#"C" or (NOT pccascadeparent=="USER_LEVEL")
  SELECT (lcChildWkArea)
  lcChildID=GROUP_ID
  lcOldChildID=oldval("GROUP_ID")
  pcChildDBF=dbf(lcChildWkArea)
  pnChildRec=recno(lcChildWkArea)
  pcChildID=lcOldChildID
  pcChildExpr="GROUP_ID"
  IF lcChildID<>lcOldChildID
    lcParentWkArea=riopen("user_level","group_id")
    IF lcParentWkArea<=0
      IF _triggerlevel=1
        DO riend WITH .F.
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN .F.
    ENDIF not able to open the child work area
    pcParentDBF=dbf(lcParentWkArea)
    llRetVal=SEEK(lcChildID,lcParentWkArea)
    pnParentRec=recno(lcParentWkArea)
    =rireuse("user_level",lcParentWkArea)
    IF NOT llRetVal
      DO rierror with -1,"Insert restrict rule violated.","",""
      IF _triggerlevel=1
        DO riend WITH llRetVal
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN llRetVal
    ENDIF no parent
  ENDIF this value was changed
ENDIF not part of a cascade from "user_level"
lcParentWkArea=lcChildWkArea
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
lcOldParentID=OLDVAL("EMPLOYEE_ID")
pcParentID=lcOldParentID
pcParentExpr="EMPLOYEE_ID"
lcParentID=EMPLOYEE_ID
IF lcParentID<>lcOldParentID
  lcChildWkArea=riopen("orders")
  IF lcChildWkArea<=0
    IF _triggerlevel=1
      DO riend WITH .F.
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN .F.
  ENDIF not able to open the child work area
  pcChildDBF=dbf(lcChildWkArea)
  SELECT (lcChildWkArea)
  SCAN FOR EMPLOYEE_ID=lcOldParentID
    pnChildRec=recno()
    pcChildID=EMPLOYEE_ID
    pcChildExpr="EMPLOYEE_ID"
    IF NOT llRetVal
      EXIT
    ENDIF && not llretval
    llRetVal=riupdate("EMPLOYEE_ID",lcParentID,"EMPLOYEE")
  ENDSCAN get all of the orders records
  =rireuse("orders",lcChildWkArea)
  IF NOT llRetVal
    IF _triggerlevel=1
      DO riend WITH llRetVal
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN llRetVal
  ENDIF
ENDIF this parent id changed
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Update trigger for" employee
********************************************************************************

********************************************************************************
** "Referential integrity insert trigger for" employee
PROCEDURE __RI_INSERT_employee
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcChildID && child's value to be sought in parent
LOCAL lcParentWkArea && parentwork area handle returned by riopen
LOCAL lcChildWkArea && child's work area
LOCAL lcStartArea
lcStartArea=select()
llRetVal=.t.
lcChildWkArea=SELECT()
SELECT (lcChildWkArea)
lcChildID=GROUP_ID
pcChildDBF=dbf(lcChildWkArea)
pnChildRec=recno(lcChildWkArea)
pcChildID=lcChildID
pcChildExpr="GROUP_ID"
lcParentWkArea=riopen("user_level","group_id")
IF lcParentWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN .F.
ENDIF not able to open the child work area
pcParentDBF=dbf(lcParentWkArea)
llRetVal=SEEK(lcChildID,lcParentWkArea)
pnParentRec=recno(lcParentWkArea)
=rireuse("user_level",lcParentWkArea)
IF NOT llRetVal
  DO rierror with -1,"Insert restrict rule violated.","",""
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity insert trigger for" employee
********************************************************************************

********************************************************************************
procedure __RI_UPDATE_order_line_items
** "Referential integrity update trigger for" order_line_items
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcOldParentID && previous parent id value
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcChildID && child's value to be sought in parent
LOCAL lcOldChildID && old child id value
LOCAL lcParentWkArea && parentwork area handle returned by riopen
LOCAL lcStartArea
lcStartArea=select()
llRetVal=.t.
lcChildWkArea=select()
IF _triggerlevel=1 or type("pccascadeparent")#"C" or (NOT pccascadeparent=="PRODUCTS")
  SELECT (lcChildWkArea)
  lcChildID=PRODUCT_ID
  lcOldChildID=oldval("PRODUCT_ID")
  pcChildDBF=dbf(lcChildWkArea)
  pnChildRec=recno(lcChildWkArea)
  pcChildID=lcOldChildID
  pcChildExpr="PRODUCT_ID"
  IF lcChildID<>lcOldChildID
    lcParentWkArea=riopen("products","product_id")
    IF lcParentWkArea<=0
      IF _triggerlevel=1
        DO riend WITH .F.
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN .F.
    ENDIF not able to open the child work area
    pcParentDBF=dbf(lcParentWkArea)
    llRetVal=SEEK(lcChildID,lcParentWkArea)
    pnParentRec=recno(lcParentWkArea)
    =rireuse("products",lcParentWkArea)
    IF NOT llRetVal
      DO rierror with -1,"Insert restrict rule violated.","",""
      IF _triggerlevel=1
        DO riend WITH llRetVal
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN llRetVal
    ENDIF no parent
  ENDIF this value was changed
ENDIF not part of a cascade from "products"
IF _triggerlevel=1 or type("pccascadeparent")#"C" or (NOT pccascadeparent=="ORDERS")
  SELECT (lcChildWkArea)
  lcChildID=ORDER_ID
  lcOldChildID=oldval("ORDER_ID")
  pcChildDBF=dbf(lcChildWkArea)
  pnChildRec=recno(lcChildWkArea)
  pcChildID=lcOldChildID
  pcChildExpr="ORDER_ID"
  IF lcChildID<>lcOldChildID
    lcParentWkArea=riopen("orders","order_id")
    IF lcParentWkArea<=0
      IF _triggerlevel=1
        DO riend WITH .F.
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN .F.
    ENDIF not able to open the child work area
    pcParentDBF=dbf(lcParentWkArea)
    llRetVal=SEEK(lcChildID,lcParentWkArea)
    pnParentRec=recno(lcParentWkArea)
    =rireuse("orders",lcParentWkArea)
    IF NOT llRetVal
      DO rierror with -1,"Insert restrict rule violated.","",""
      IF _triggerlevel=1
        DO riend WITH llRetVal
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN llRetVal
    ENDIF no parent
  ENDIF this value was changed
ENDIF not part of a cascade from "orders"
lcParentWkArea=lcChildWkArea
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Update trigger for" order_line_items
********************************************************************************

********************************************************************************
** "Referential integrity insert trigger for" order_line_items
PROCEDURE __RI_INSERT_order_line_items
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcChildID && child's value to be sought in parent
LOCAL lcParentWkArea && parentwork area handle returned by riopen
LOCAL lcChildWkArea && child's work area
LOCAL lcStartArea
lcStartArea=select()
llRetVal=.t.
lcChildWkArea=SELECT()
SELECT (lcChildWkArea)
lcChildID=PRODUCT_ID
pcChildDBF=dbf(lcChildWkArea)
pnChildRec=recno(lcChildWkArea)
pcChildID=lcChildID
pcChildExpr="PRODUCT_ID"
lcParentWkArea=riopen("products","product_id")
IF lcParentWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN .F.
ENDIF not able to open the child work area
pcParentDBF=dbf(lcParentWkArea)
llRetVal=SEEK(lcChildID,lcParentWkArea)
pnParentRec=recno(lcParentWkArea)
=rireuse("products",lcParentWkArea)
IF NOT llRetVal
  DO rierror with -1,"Insert restrict rule violated.","",""
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
SELECT (lcChildWkArea)
lcChildID=ORDER_ID
pcChildDBF=dbf(lcChildWkArea)
pnChildRec=recno(lcChildWkArea)
pcChildID=lcChildID
pcChildExpr="ORDER_ID"
lcParentWkArea=riopen("orders","order_id")
IF lcParentWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN .F.
ENDIF not able to open the child work area
pcParentDBF=dbf(lcParentWkArea)
llRetVal=SEEK(lcChildID,lcParentWkArea)
pnParentRec=recno(lcParentWkArea)
=rireuse("orders",lcParentWkArea)
IF NOT llRetVal
  DO rierror with -1,"Insert restrict rule violated.","",""
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity insert trigger for" order_line_items
********************************************************************************

********************************************************************************
** "Referential integrity delete trigger for" orders
PROCEDURE __RI_DELETE_orders
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcParentWkArea
LOCAL llDelHeaderarea
lcStartArea=select()
llRetVal=.t.
lcParentWkArea=select()
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
STORE ORDER_ID TO lcParentID,pcParentID
pcParentExpr="ORDER_ID"
lcChildWkArea=riopen("order_line_items","order_id")
IF lcChildWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  RETURN .F.
ENDIF not able to open the child work area
pcChildDBF=dbf(lcChildWkArea)
SELECT (lcChildWkArea)
SEEK lcParentID
SCAN WHILE ORDER_ID=lcParentID AND llRetVal
  pnChildRec=recno()
  pcChildID=ORDER_ID
  pcChildExpr="ORDER_ID"
  llRetVal=ridelete()
ENDSCAN get all of the order_line_items records
=rireuse("order_line_items",lcChildWkArea)
IF NOT llRetVal
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Delete trigger for" orders
********************************************************************************

********************************************************************************
procedure __RI_UPDATE_orders
** "Referential integrity update trigger for" orders
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcOldParentID && previous parent id value
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcChildID && child's value to be sought in parent
LOCAL lcOldChildID && old child id value
LOCAL lcParentWkArea && parentwork area handle returned by riopen
LOCAL lcStartArea
lcStartArea=select()
llRetVal=.t.
lcChildWkArea=select()
IF _triggerlevel=1 or type("pccascadeparent")#"C" or (NOT pccascadeparent=="CUSTOMER")
  SELECT (lcChildWkArea)
  lcChildID=CUSTOMER_ID
  lcOldChildID=oldval("CUSTOMER_ID")
  pcChildDBF=dbf(lcChildWkArea)
  pnChildRec=recno(lcChildWkArea)
  pcChildID=lcOldChildID
  pcChildExpr="CUSTOMER_ID"
  IF lcChildID<>lcOldChildID
    lcParentWkArea=riopen("customer","customer_i")
    IF lcParentWkArea<=0
      IF _triggerlevel=1
        DO riend WITH .F.
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN .F.
    ENDIF not able to open the child work area
    pcParentDBF=dbf(lcParentWkArea)
    llRetVal=SEEK(lcChildID,lcParentWkArea)
    pnParentRec=recno(lcParentWkArea)
    =rireuse("customer",lcParentWkArea)
    IF NOT llRetVal
      DO rierror with -1,"Insert restrict rule violated.","",""
      IF _triggerlevel=1
        DO riend WITH llRetVal
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN llRetVal
    ENDIF no parent
  ENDIF this value was changed
ENDIF not part of a cascade from "customer"
IF _triggerlevel=1 or type("pccascadeparent")#"C" or (NOT pccascadeparent=="SHIPPERS")
  SELECT (lcChildWkArea)
  lcChildID=SHIPPER_ID
  lcOldChildID=oldval("SHIPPER_ID")
  pcChildDBF=dbf(lcChildWkArea)
  pnChildRec=recno(lcChildWkArea)
  pcChildID=lcOldChildID
  pcChildExpr="SHIPPER_ID"
  IF lcChildID<>lcOldChildID
    lcParentWkArea=riopen("shippers","shipper_id")
    IF lcParentWkArea<=0
      IF _triggerlevel=1
        DO riend WITH .F.
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN .F.
    ENDIF not able to open the child work area
    pcParentDBF=dbf(lcParentWkArea)
    llRetVal=SEEK(lcChildID,lcParentWkArea)
    pnParentRec=recno(lcParentWkArea)
    =rireuse("shippers",lcParentWkArea)
    IF NOT llRetVal
      DO rierror with -1,"Insert restrict rule violated.","",""
      IF _triggerlevel=1
        DO riend WITH llRetVal
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN llRetVal
    ENDIF no parent
  ENDIF this value was changed
ENDIF not part of a cascade from "shippers"
IF _triggerlevel=1 or type("pccascadeparent")#"C" or (NOT pccascadeparent=="EMPLOYEE")
  SELECT (lcChildWkArea)
  lcChildID=EMPLOYEE_ID
  lcOldChildID=oldval("EMPLOYEE_ID")
  pcChildDBF=dbf(lcChildWkArea)
  pnChildRec=recno(lcChildWkArea)
  pcChildID=lcOldChildID
  pcChildExpr="EMPLOYEE_ID"
  IF lcChildID<>lcOldChildID
    lcParentWkArea=riopen("employee","employee_i")
    IF lcParentWkArea<=0
      IF _triggerlevel=1
        DO riend WITH .F.
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN .F.
    ENDIF not able to open the child work area
    pcParentDBF=dbf(lcParentWkArea)
    llRetVal=SEEK(lcChildID,lcParentWkArea)
    pnParentRec=recno(lcParentWkArea)
    =rireuse("employee",lcParentWkArea)
    IF NOT llRetVal
      DO rierror with -1,"Insert restrict rule violated.","",""
      IF _triggerlevel=1
        DO riend WITH llRetVal
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN llRetVal
    ENDIF no parent
  ENDIF this value was changed
ENDIF not part of a cascade from "employee"
lcParentWkArea=lcChildWkArea
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
lcOldParentID=OLDVAL("ORDER_ID")
pcParentID=lcOldParentID
pcParentExpr="ORDER_ID"
lcParentID=ORDER_ID
IF lcParentID<>lcOldParentID
  lcChildWkArea=riopen("order_line_items")
  IF lcChildWkArea<=0
    IF _triggerlevel=1
      DO riend WITH .F.
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN .F.
  ENDIF not able to open the child work area
  pcChildDBF=dbf(lcChildWkArea)
  SELECT (lcChildWkArea)
  SCAN FOR ORDER_ID=lcOldParentID
    pnChildRec=recno()
    pcChildID=ORDER_ID
    pcChildExpr="ORDER_ID"
    IF NOT llRetVal
      EXIT
    ENDIF && not llretval
    llRetVal=riupdate("ORDER_ID",lcParentID,"ORDERS")
  ENDSCAN get all of the order_line_items records
  =rireuse("order_line_items",lcChildWkArea)
  IF NOT llRetVal
    IF _triggerlevel=1
      DO riend WITH llRetVal
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN llRetVal
  ENDIF
ENDIF this parent id changed
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Update trigger for" orders
********************************************************************************

********************************************************************************
** "Referential integrity insert trigger for" orders
PROCEDURE __RI_INSERT_orders
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcChildID && child's value to be sought in parent
LOCAL lcParentWkArea && parentwork area handle returned by riopen
LOCAL lcChildWkArea && child's work area
LOCAL lcStartArea
lcStartArea=select()
llRetVal=.t.
lcChildWkArea=SELECT()
SELECT (lcChildWkArea)
lcChildID=CUSTOMER_ID
pcChildDBF=dbf(lcChildWkArea)
pnChildRec=recno(lcChildWkArea)
pcChildID=lcChildID
pcChildExpr="CUSTOMER_ID"
lcParentWkArea=riopen("customer","customer_i")
IF lcParentWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN .F.
ENDIF not able to open the child work area
pcParentDBF=dbf(lcParentWkArea)
llRetVal=SEEK(lcChildID,lcParentWkArea)
pnParentRec=recno(lcParentWkArea)
=rireuse("customer",lcParentWkArea)
IF NOT llRetVal
  DO rierror with -1,"Insert restrict rule violated.","",""
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
SELECT (lcChildWkArea)
lcChildID=SHIPPER_ID
pcChildDBF=dbf(lcChildWkArea)
pnChildRec=recno(lcChildWkArea)
pcChildID=lcChildID
pcChildExpr="SHIPPER_ID"
lcParentWkArea=riopen("shippers","shipper_id")
IF lcParentWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN .F.
ENDIF not able to open the child work area
pcParentDBF=dbf(lcParentWkArea)
llRetVal=SEEK(lcChildID,lcParentWkArea)
pnParentRec=recno(lcParentWkArea)
=rireuse("shippers",lcParentWkArea)
IF NOT llRetVal
  DO rierror with -1,"Insert restrict rule violated.","",""
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
SELECT (lcChildWkArea)
lcChildID=EMPLOYEE_ID
pcChildDBF=dbf(lcChildWkArea)
pnChildRec=recno(lcChildWkArea)
pcChildID=lcChildID
pcChildExpr="EMPLOYEE_ID"
lcParentWkArea=riopen("employee","employee_i")
IF lcParentWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN .F.
ENDIF not able to open the child work area
pcParentDBF=dbf(lcParentWkArea)
llRetVal=SEEK(lcChildID,lcParentWkArea)
pnParentRec=recno(lcParentWkArea)
=rireuse("employee",lcParentWkArea)
IF NOT llRetVal
  DO rierror with -1,"Insert restrict rule violated.","",""
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity insert trigger for" orders
********************************************************************************

********************************************************************************
** "Referential integrity delete trigger for" products
PROCEDURE __RI_DELETE_products
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcParentWkArea
LOCAL llDelHeaderarea
lcStartArea=select()
llRetVal=.t.
lcParentWkArea=select()
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
STORE PRODUCT_ID TO lcParentID,pcParentID
pcParentExpr="PRODUCT_ID"
lcChildWkArea=riopen("order_line_items","product_id")
IF lcChildWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  RETURN .F.
ENDIF not able to open the child work area
pcChildDBF=dbf(lcChildWkArea)
llRetVal=!SEEK(lcParentID,lcChildWkArea)
SELECT (lcChildWkArea)
pnChildRec=recno()
pcChildID=PRODUCT_ID
pcChildExpr="PRODUCT_ID"
IF !llRetVal
  DO rierror with -1,"Delete restrict rule violated.","",""
ENDIF
=rireuse("order_line_items",lcChildWkArea)
IF NOT llRetVal
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Delete trigger for" products
********************************************************************************

********************************************************************************
procedure __RI_UPDATE_products
** "Referential integrity update trigger for" products
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcOldParentID && previous parent id value
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcChildID && child's value to be sought in parent
LOCAL lcOldChildID && old child id value
LOCAL lcParentWkArea && parentwork area handle returned by riopen
LOCAL lcStartArea
lcStartArea=select()
llRetVal=.t.
lcChildWkArea=select()
IF _triggerlevel=1 or type("pccascadeparent")#"C" or (NOT pccascadeparent=="CATEGORY")
  SELECT (lcChildWkArea)
  lcChildID=CATEGORY_ID
  lcOldChildID=oldval("CATEGORY_ID")
  pcChildDBF=dbf(lcChildWkArea)
  pnChildRec=recno(lcChildWkArea)
  pcChildID=lcOldChildID
  pcChildExpr="CATEGORY_ID"
  IF lcChildID<>lcOldChildID
    lcParentWkArea=riopen("category","category_i")
    IF lcParentWkArea<=0
      IF _triggerlevel=1
        DO riend WITH .F.
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN .F.
    ENDIF not able to open the child work area
    pcParentDBF=dbf(lcParentWkArea)
    llRetVal=SEEK(lcChildID,lcParentWkArea)
    pnParentRec=recno(lcParentWkArea)
    =rireuse("category",lcParentWkArea)
    IF NOT llRetVal
      DO rierror with -1,"Insert restrict rule violated.","",""
      IF _triggerlevel=1
        DO riend WITH llRetVal
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN llRetVal
    ENDIF no parent
  ENDIF this value was changed
ENDIF not part of a cascade from "category"
IF _triggerlevel=1 or type("pccascadeparent")#"C" or (NOT pccascadeparent=="SUPPLIER")
  SELECT (lcChildWkArea)
  lcChildID=SUPPLIER_ID
  lcOldChildID=oldval("SUPPLIER_ID")
  pcChildDBF=dbf(lcChildWkArea)
  pnChildRec=recno(lcChildWkArea)
  pcChildID=lcOldChildID
  pcChildExpr="SUPPLIER_ID"
  IF lcChildID<>lcOldChildID
    lcParentWkArea=riopen("supplier","supplier_i")
    IF lcParentWkArea<=0
      IF _triggerlevel=1
        DO riend WITH .F.
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN .F.
    ENDIF not able to open the child work area
    pcParentDBF=dbf(lcParentWkArea)
    llRetVal=SEEK(lcChildID,lcParentWkArea)
    pnParentRec=recno(lcParentWkArea)
    =rireuse("supplier",lcParentWkArea)
    IF NOT llRetVal
      DO rierror with -1,"Insert restrict rule violated.","",""
      IF _triggerlevel=1
        DO riend WITH llRetVal
      ENDIF at the end of the highest trigger level
      SELECT (lcStartArea)
      RETURN llRetVal
    ENDIF no parent
  ENDIF this value was changed
ENDIF not part of a cascade from "supplier"
lcParentWkArea=lcChildWkArea
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
lcOldParentID=OLDVAL("PRODUCT_ID")
pcParentID=lcOldParentID
pcParentExpr="PRODUCT_ID"
lcParentID=PRODUCT_ID
IF lcParentID<>lcOldParentID
  lcChildWkArea=riopen("order_line_items")
  IF lcChildWkArea<=0
    IF _triggerlevel=1
      DO riend WITH .F.
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN .F.
  ENDIF not able to open the child work area
  pcChildDBF=dbf(lcChildWkArea)
  SELECT (lcChildWkArea)
  SCAN FOR PRODUCT_ID=lcOldParentID
    pnChildRec=recno()
    pcChildID=PRODUCT_ID
    pcChildExpr="PRODUCT_ID"
    IF NOT llRetVal
      EXIT
    ENDIF && not llretval
    llRetVal=riupdate("PRODUCT_ID",lcParentID,"PRODUCTS")
  ENDSCAN get all of the order_line_items records
  =rireuse("order_line_items",lcChildWkArea)
  IF NOT llRetVal
    IF _triggerlevel=1
      DO riend WITH llRetVal
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN llRetVal
  ENDIF
ENDIF this parent id changed
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Update trigger for" products
********************************************************************************

********************************************************************************
** "Referential integrity insert trigger for" products
PROCEDURE __RI_INSERT_products
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcChildID && child's value to be sought in parent
LOCAL lcParentWkArea && parentwork area handle returned by riopen
LOCAL lcChildWkArea && child's work area
LOCAL lcStartArea
lcStartArea=select()
llRetVal=.t.
lcChildWkArea=SELECT()
SELECT (lcChildWkArea)
lcChildID=CATEGORY_ID
pcChildDBF=dbf(lcChildWkArea)
pnChildRec=recno(lcChildWkArea)
pcChildID=lcChildID
pcChildExpr="CATEGORY_ID"
lcParentWkArea=riopen("category","category_i")
IF lcParentWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN .F.
ENDIF not able to open the child work area
pcParentDBF=dbf(lcParentWkArea)
llRetVal=SEEK(lcChildID,lcParentWkArea)
pnParentRec=recno(lcParentWkArea)
=rireuse("category",lcParentWkArea)
IF NOT llRetVal
  DO rierror with -1,"Insert restrict rule violated.","",""
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
SELECT (lcChildWkArea)
lcChildID=SUPPLIER_ID
pcChildDBF=dbf(lcChildWkArea)
pnChildRec=recno(lcChildWkArea)
pcChildID=lcChildID
pcChildExpr="SUPPLIER_ID"
lcParentWkArea=riopen("supplier","supplier_i")
IF lcParentWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN .F.
ENDIF not able to open the child work area
pcParentDBF=dbf(lcParentWkArea)
llRetVal=SEEK(lcChildID,lcParentWkArea)
pnParentRec=recno(lcParentWkArea)
=rireuse("supplier",lcParentWkArea)
IF NOT llRetVal
  DO rierror with -1,"Insert restrict rule violated.","",""
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity insert trigger for" products
********************************************************************************

********************************************************************************
** "Referential integrity delete trigger for" shippers
PROCEDURE __RI_DELETE_shippers
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcParentWkArea
LOCAL llDelHeaderarea
lcStartArea=select()
llRetVal=.t.
lcParentWkArea=select()
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
STORE SHIPPER_ID TO lcParentID,pcParentID
pcParentExpr="SHIPPER_ID"
lcChildWkArea=riopen("orders","shipper_id")
IF lcChildWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  RETURN .F.
ENDIF not able to open the child work area
pcChildDBF=dbf(lcChildWkArea)
llRetVal=!SEEK(lcParentID,lcChildWkArea)
SELECT (lcChildWkArea)
pnChildRec=recno()
pcChildID=SHIPPER_ID
pcChildExpr="SHIPPER_ID"
IF !llRetVal
  DO rierror with -1,"Delete restrict rule violated.","",""
ENDIF
=rireuse("orders",lcChildWkArea)
IF NOT llRetVal
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Delete trigger for" shippers
********************************************************************************

********************************************************************************
procedure __RI_UPDATE_shippers
** "Referential integrity update trigger for" shippers
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcOldParentID && previous parent id value
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcChildID && child's value to be sought in parent
LOCAL lcOldChildID && old child id value
LOCAL lcParentWkArea && parentwork area handle returned by riopen
LOCAL lcStartArea
lcStartArea=select()
llRetVal=.t.
lcParentWkArea=select()
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
lcOldParentID=OLDVAL("SHIPPER_ID")
pcParentID=lcOldParentID
pcParentExpr="SHIPPER_ID"
lcParentID=SHIPPER_ID
IF lcParentID<>lcOldParentID
  lcChildWkArea=riopen("orders")
  IF lcChildWkArea<=0
    IF _triggerlevel=1
      DO riend WITH .F.
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN .F.
  ENDIF not able to open the child work area
  pcChildDBF=dbf(lcChildWkArea)
  SELECT (lcChildWkArea)
  SCAN FOR SHIPPER_ID=lcOldParentID
    pnChildRec=recno()
    pcChildID=SHIPPER_ID
    pcChildExpr="SHIPPER_ID"
    IF NOT llRetVal
      EXIT
    ENDIF && not llretval
    llRetVal=riupdate("SHIPPER_ID",lcParentID,"SHIPPERS")
  ENDSCAN get all of the orders records
  =rireuse("orders",lcChildWkArea)
  IF NOT llRetVal
    IF _triggerlevel=1
      DO riend WITH llRetVal
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN llRetVal
  ENDIF
ENDIF this parent id changed
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Update trigger for" shippers
********************************************************************************

********************************************************************************
** "Referential integrity delete trigger for" supplier
PROCEDURE __RI_DELETE_supplier
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcParentWkArea
LOCAL llDelHeaderarea
lcStartArea=select()
llRetVal=.t.
lcParentWkArea=select()
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
STORE SUPPLIER_ID TO lcParentID,pcParentID
pcParentExpr="SUPPLIER_ID"
lcChildWkArea=riopen("products","supplier_i")
IF lcChildWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  RETURN .F.
ENDIF not able to open the child work area
pcChildDBF=dbf(lcChildWkArea)
llRetVal=!SEEK(lcParentID,lcChildWkArea)
SELECT (lcChildWkArea)
pnChildRec=recno()
pcChildID=SUPPLIER_ID
pcChildExpr="SUPPLIER_ID"
IF !llRetVal
  DO rierror with -1,"Delete restrict rule violated.","",""
ENDIF
=rireuse("products",lcChildWkArea)
IF NOT llRetVal
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Delete trigger for" supplier
********************************************************************************

********************************************************************************
procedure __RI_UPDATE_supplier
** "Referential integrity update trigger for" supplier
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcOldParentID && previous parent id value
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcChildID && child's value to be sought in parent
LOCAL lcOldChildID && old child id value
LOCAL lcParentWkArea && parentwork area handle returned by riopen
LOCAL lcStartArea
lcStartArea=select()
llRetVal=.t.
lcParentWkArea=select()
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
lcOldParentID=OLDVAL("SUPPLIER_ID")
pcParentID=lcOldParentID
pcParentExpr="SUPPLIER_ID"
lcParentID=SUPPLIER_ID
IF lcParentID<>lcOldParentID
  lcChildWkArea=riopen("products")
  IF lcChildWkArea<=0
    IF _triggerlevel=1
      DO riend WITH .F.
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN .F.
  ENDIF not able to open the child work area
  pcChildDBF=dbf(lcChildWkArea)
  SELECT (lcChildWkArea)
  SCAN FOR SUPPLIER_ID=lcOldParentID
    pnChildRec=recno()
    pcChildID=SUPPLIER_ID
    pcChildExpr="SUPPLIER_ID"
    IF NOT llRetVal
      EXIT
    ENDIF && not llretval
    llRetVal=riupdate("SUPPLIER_ID",lcParentID,"SUPPLIER")
  ENDSCAN get all of the products records
  =rireuse("products",lcChildWkArea)
  IF NOT llRetVal
    IF _triggerlevel=1
      DO riend WITH llRetVal
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN llRetVal
  ENDIF
ENDIF this parent id changed
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Update trigger for" supplier
********************************************************************************

********************************************************************************
** "Referential integrity delete trigger for" user_level
PROCEDURE __RI_DELETE_user_level
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcParentWkArea
LOCAL llDelHeaderarea
lcStartArea=select()
llRetVal=.t.
lcParentWkArea=select()
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
STORE GROUP_ID TO lcParentID,pcParentID
pcParentExpr="GROUP_ID"
lcChildWkArea=riopen("employee","group_id")
IF lcChildWkArea<=0
  IF _triggerlevel=1
    DO riend WITH .F.
  ENDIF at the end of the highest trigger level
  RETURN .F.
ENDIF not able to open the child work area
pcChildDBF=dbf(lcChildWkArea)
llRetVal=!SEEK(lcParentID,lcChildWkArea)
SELECT (lcChildWkArea)
pnChildRec=recno()
pcChildID=GROUP_ID
pcChildExpr="GROUP_ID"
IF !llRetVal
  DO rierror with -1,"Delete restrict rule violated.","",""
ENDIF
=rireuse("employee",lcChildWkArea)
IF NOT llRetVal
  IF _triggerlevel=1
    DO riend WITH llRetVal
  ENDIF at the end of the highest trigger level
  SELECT (lcStartArea)
  RETURN llRetVal
ENDIF
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Delete trigger for" user_level
********************************************************************************

********************************************************************************
procedure __RI_UPDATE_user_level
** "Referential integrity update trigger for" user_level
LOCAL llRetVal
llRetVal = .t.
PRIVATE pcParentDBF,pnParentRec,pcChildDBF,pnChildRec,pcParentID,pcChildID
PRIVATE pcParentExpr,pcChildExpr
STORE "" TO pcParentDBF,pcChildDBF,pcParentID,pcChildID,pcParentExpr,pcChildExpr
STORE 0 TO pnParentRec,pnChildRec
IF _triggerlevel=1
  BEGIN TRANSACTION
  PRIVATE pcRIcursors,pcRIwkareas,pcRIolderror,pnerror,;
  pcOldDele,pcOldExact,pcOldTalk,pcOldCompat,PcOldDBC
  pcOldTalk=SET("TALK")
  SET TALK OFF
  pcOldDele=SET("DELETED")
  pcOldExact=SET("EXACT")
  pcOldCompat=SET("COMPATIBLE")
  SET COMPATIBLE OFF
  SET DELETED ON
  SET EXACT OFF
  pcRIcursors=""
  pcRIwkareas=""
  pcRIolderror=ON("error")
  pnerror=0
  ON ERROR pnerror=rierror(ERROR(),message(),message(1),program())
  IF TYPE('gaErrors(1)')<>"U"
    release gaErrors
  ENDIF
  PUBLIC gaErrors(1,12)
  pcOldDBC=DBC()
  SET DATA TO ("TASTRADE")
ENDIF first trigger
LOCAL lcParentID && parent's value to be sought in child
LOCAL lcOldParentID && previous parent id value
LOCAL lcChildWkArea && child work area handle returned by riopen
LOCAL lcChildID && child's value to be sought in parent
LOCAL lcOldChildID && old child id value
LOCAL lcParentWkArea && parentwork area handle returned by riopen
LOCAL lcStartArea
lcStartArea=select()
llRetVal=.t.
lcParentWkArea=select()
SELECT (lcParentWkArea)
pcParentDBF=dbf()
pnParentRec=recno()
lcOldParentID=OLDVAL("GROUP_ID")
pcParentID=lcOldParentID
pcParentExpr="GROUP_ID"
lcParentID=GROUP_ID
IF lcParentID<>lcOldParentID
  lcChildWkArea=riopen("employee")
  IF lcChildWkArea<=0
    IF _triggerlevel=1
      DO riend WITH .F.
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN .F.
  ENDIF not able to open the child work area
  pcChildDBF=dbf(lcChildWkArea)
  SELECT (lcChildWkArea)
  SCAN FOR GROUP_ID=lcOldParentID
    pnChildRec=recno()
    pcChildID=GROUP_ID
    pcChildExpr="GROUP_ID"
    IF NOT llRetVal
      EXIT
    ENDIF && not llretval
    llRetVal=riupdate("GROUP_ID",lcParentID,"USER_LEVEL")
  ENDSCAN get all of the employee records
  =rireuse("employee",lcChildWkArea)
  IF NOT llRetVal
    IF _triggerlevel=1
      DO riend WITH llRetVal
    ENDIF at the end of the highest trigger level
    SELECT (lcStartArea)
    RETURN llRetVal
  ENDIF
ENDIF this parent id changed
IF _triggerlevel=1
  do riend with llRetVal
ENDIF at the end of the highest trigger level
SELECT (lcStartArea)
RETURN llRetVal
** "End of Referential integrity Update trigger for" user_level
********************************************************************************
**__RI_FOOTER!@ Do NOT REMOVE or MODIFY this line!!!! @!__RI_FOOTER**
