module AST exposing (..)

-- builtin
import List

-- lib
import String.Extra as SE
import List.Extra as LE

-- dark
import Types exposing (..)
import Runtime as RT

import Util exposing (deMaybe)

depthString : Int -> String
depthString n = "precedence-" ++ (toString n)

-- v is short for Visit

vFn : FnName -> Element
vFn name =
  case String.split "::" name of
    [mod, n] ->
      Nested "namegroup atom"
      [ Leaf (Nothing, "module", mod)
      , Leaf (Nothing, "moduleseparator", "::")
      , Leaf (Nothing, "fnname", n)
      ]
    _ -> Leaf (Nothing, "fnname atom", name)

vPrefix : FnName -> List Expr -> Int -> Element
vPrefix name exprs nest =
  Nested ("fncall prefix " ++ (depthString nest))
    ((Nested ("op " ++ name) [vFn name])
    :: (List.map (vExpr (nest + 1)) exprs))


vInfix : FnName -> List Expr -> Int -> Element
vInfix name exprs nesting =
  case exprs of
    [first, second] ->
      Nested ("fncall infix " ++ (depthString nesting))
        [ Nested "lhs" [vExpr (nesting + 1) first]
        , Nested ("op " ++ name) [vFn name]
        , Nested "rhs" [vExpr nesting second]
        ]
    _ -> vPrefix ("(" ++ name ++ ")") exprs nesting

isInfix : FnName -> Bool
isInfix name =
  List.member name ["<", "==", "%", "+"]

vVarname : VarName -> Element
vVarname v = Leaf (Nothing, "varname atom", v)

vVarBind : VarBind -> Element
vVarBind v =
  case v of
    Named s -> Leaf (Nothing, "varname atom", s)
    BindHole id -> Leaf (Just id, "hole atom", "＿＿＿＿＿＿")

vExpr : Int -> Expr -> Element
vExpr nest expr =
  case expr of
    Value v ->
     let cssClass = v |> RT.tipeOf |> toString |> String.toLower
         valu  =
           -- TODO: remove
           if RT.isString v
           then "“" ++ (SE.unquote v) ++ "”"
           else v
     in  Leaf (Nothing, "atom value " ++ cssClass, valu)

    Let vars expr ->
      Nested "letexpr"
        [ Leaf (Nothing, "let keyword atom", "let")
        , Nested "letbindings"
            (List.map
              (\(l, r) ->
                Nested "letbinding"
                  [ vVarBind l
                  , Leaf (Nothing, "letbind atom", "=")
                  , vExpr nest r
                  ]
              )
              vars
             )
        , Leaf (Nothing, "in keyword atom" , "in")
        , Nested "letbody" [vExpr nest expr]
        ]


    If cond ifbody elsebody ->
      Nested "ifexpr"
        [ Leaf (Nothing, "if keyword atom", "if")
        , Nested "cond" [vExpr (nest + 1) cond]
        , Nested "ifbody" [(vExpr 0 ifbody)]
        , Leaf (Nothing, "else keyword atom", "else")
        , Nested "elsebody" [(vExpr 0 elsebody)]
        ]

    Variable name ->
      vVarname name

    FnCall name exprs ->
      if isInfix name
      then vInfix name exprs nest
      else vPrefix name exprs nest

    Lambda vars expr ->
      Nested "lambdaexpr"
        [ Nested "lambdabinding" (List.map vVarname vars)
        , Leaf (Nothing, "arrow atom" , "->")
        , Nested "lambdabody" [vExpr 0 expr]
        ]

    Hole id -> Leaf (Just id, "hole atom", "＿＿＿＿＿＿")

replaceHole : HID -> Expr -> AST -> AST
replaceHole hid replacement ast =
  replaceHole_ hid replacement ast

replaceHole_ : HID -> Expr -> Expr -> Expr
replaceHole_ hid replacement expr =
  let rh = replaceHole_ hid replacement
      rhList : List Expr -> List Expr
      rhList exprs = List.map rh exprs
  in
  case expr of
    Value v ->
      Value v

    Let vars expr ->
      let vs = List.map (\(vb, e) ->
         case vb of
           -- TODO: replace this with a different replaceBindHole
           -- that gets called from the submit of a special entry box
           Named s -> (Named s, rh e)
           BindHole id ->
             if id == hid
             then
               case replacement of
                 Value s -> (Named (SE.unquote s), e)
                 _       -> (BindHole id, e)
             else (BindHole id, rh e)
             ) vars
      in Let vs (rh expr)

    If cond ifbody elsebody ->
      If (rh cond) (rh ifbody) (rh elsebody)

    Variable name ->
      Variable name

    FnCall name exprs ->
      FnCall name (rhList exprs)

    Lambda vars expr ->
      Lambda vars (rh expr)

    Hole id ->
      if id == hid
      then replacement
      else expr


bindHoleHID : VarBind -> Maybe HID
bindHoleHID vb =
  case vb of
    BindHole hid -> Just hid
    Named _ -> Nothing

listHoles : Expr -> List HID
listHoles expr =
  let lhList : List Expr -> List HID
      lhList exprs =
        exprs
        |> List.map listHoles
        |> List.concat
      bhList : List VarBind -> List HID
      bhList = List.filterMap bindHoleHID
  in
  case expr of
    Value v ->
      []

    Let vars expr ->
      let exprHoles = vars
                      |> List.map Tuple.second
                      |> (++) [expr]
                      |> lhList
          bindHoles = vars
                    |> List.map Tuple.first
                    |> bhList
      in
          bindHoles ++ exprHoles

    If cond ifbody elsebody ->
      lhList [cond, ifbody, elsebody]

    Variable name ->
      []

    FnCall name exprs ->
      lhList exprs

    Lambda vars expr ->
      listHoles expr

    Hole id -> [id]

findNextHole : HID -> AST -> HID
findNextHole cur ast =
  let holes = listHoles ast
  in case (LE.dropWhile (\x -> x /= cur) holes) of
     cur :: next :: _ -> next
     [cur] -> holes |> List.head |> deMaybe
     [] -> HID 237

walk : AST -> Element
walk = vExpr 0
