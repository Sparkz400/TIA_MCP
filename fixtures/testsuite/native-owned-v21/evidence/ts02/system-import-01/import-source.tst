TEST_CASE "SingleStep"
PROPERTY
AUTHOR : "Tom"
VERSION : "0.1"
COMMENT : "This testcase contains single step"
SERVER_ADDRESS : "opc.tcp://192.168.0.1:6"
FOLDER_PATH : "D:\Simatic"// The selected server interface can be User defined, Standard SIMATIC or SiOME-Companion specification
SELECTED_SERVER_INTERFACE : "Standard_SIMATIC"
END_PROPERTY
VAR
tcvar1 : fbMotor_DB.outputs.myVar1:=1;
tcvar2 : fbMotor_DB.outputs.myVar2:=1;
tcvar3 : fbMotor_DB.outputs.myVar3:=1;
END_VAR
STEP: "Step name"
wait(time:=t#2ms);
Assert.Equal(tcvar1,1);
Assert.Equal(tcvar2,1);
Assert.Equal(tcvar3,1);
END_STEP
END_TEST_CASE
