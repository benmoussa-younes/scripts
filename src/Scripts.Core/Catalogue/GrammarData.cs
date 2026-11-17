// Source of the grammar exposed by `scripts describe`. The parser is the implementation; the describe
// corpus checks that this text is still the one the binary emits.
#nullable enable

namespace Scripts.Core;

public static class GrammarData
{
    public const string Ebnf = """
program        = { NL } , { importDeclaration } , { statement } ;
importDeclaration = 'import' , STRING_PLAIN , 'as' , NAME , NL ;

statement      = ( letStatement | assignment | ifStatement | whileStatement | forStatement
                 | returnStatement | breakStatement | continueStatement | functionDeclaration
                 | expressionStatement ) , ( NL | lookahead('}') | EOF ) ;
visibility     = 'public' ;
block          = '{' , { NL } , { statement } , '}' ;

letStatement   = [ visibility ] , 'let' , [ 'mutable' ] , ( NAME | '_' ) , [ ':' , type ] , '=' , expression ;
assignment     = NAME , { '[' , expression , ']' } , assignOp , expression ;
assignOp       = '=' | '+=' | '-=' | '*=' | '/=' | '%=' ;
ifStatement    = 'if' , expression , block , { 'else' , 'if' , expression , block } , [ 'else' , block ] ;
whileStatement = 'while' , expression , block ;
forStatement   = 'for' , ( NAME | '_' ) , 'in' , expression , block ;
returnStatement= 'return' , [ expression ] ;
breakStatement = 'break' ;   continueStatement = 'continue' ;
functionDeclaration = [ visibility ] , 'function' , NAME , '(' , [ parameters ] , ')' , [ '->' , type ] , block ;
parameters     = parameter , { ',' , parameter } , [ ',' ] ;   parameter = NAME , ':' , type ;

expressionStatement = callChain , [ '?' ]  |  matchExpression ;      (* §5.7: call (optionally followed by ONE '?') or match *)
callChain      = postfix ;                                              (* must end with a call *)

matchExpression= 'match' , expression , '{' , { NL } , matchArm , { matchArm } , '}' ;
matchArm       = pattern , '=>' , ( expression | block ) , ( NL | ',' | lookahead('}') ) ;
pattern        = literalPattern , { '|' , literalPattern }              (* alternatives: literals and '_' only *)
               | '_' | NAME                                              (* NAME = binding (catch-all) *)
               | 'ok' , '(' , ( '_' | NAME ) , ')' | 'error' , '(' , ( '_' | NAME ) , ')' ;
literalPattern = [ '-' ] , INTEGER | STRING_PLAIN | 'true' | 'false' | '_' ;

type           = NAME , [ '<' , type , { ',' , type } , '>' ]
               | 'function' , '(' , [ type , { ',' , type } ] , ')' , '->' , type ;

expression     = lambda | logicalOr ;
lambda         = ( NAME | '_' | '(' , [ lambdaParam , { ',' , lambdaParam } ] , ')' ) , '=>' , expression ;
lambdaParam    = ( NAME | '_' ) , [ ':' , type ] ;                       (* annotation allowed only in parenthesized form *)
logicalOr      = logicalAnd , { 'or' , logicalAnd } ;
logicalAnd     = logicalNot , { 'and' , logicalNot } ;
logicalNot     = 'not' , logicalNot | comparison ;
comparison     = additive , [ compareOp , additive ] ;                  (* non-associative *)
compareOp      = '==' | '!=' | '<' | '<=' | '>' | '>=' ;
additive       = multiplicative , { ( '+' | '-' ) , multiplicative } ;
multiplicative = unary , { ( '*' | '/' | '%' ) , unary } ;
unary          = '-' , unary | postfix ;
postfix        = primary , { '(' , [ arguments ] , ')' | '[' , expression , ']' | '.' , NAME | '?' } ;
arguments      = [ namedArguments | positionalArguments ] ;
namedArguments = NAME , '=' , expression , { ',' , NAME , '=' , expression } , [ ',' ] ;
positionalArguments = expression , { ',' , expression } , [ ',' ] ;
primary        = INTEGER | FLOAT | 'true' | 'false' | STRING | RAW_STRING | RAW_MULTILINE_STRING | PATH_LITERAL | NAME
               | '(' , expression , ')' | listLiteral | mapLiteral
               | 'ok' , '(' , [ expression ] , ')' | errorConstructor | matchExpression ;
errorConstructor = 'error' , '(' , expression , ')' | 'error' , '(' , expression , ',' , expression , ',' , expression , ')' ;
listLiteral    = '[' , [ expression , { ',' , expression } , [ ',' ] ] , ']' ;
mapLiteral     = '{' , [ mapEntry , { ',' , mapEntry } , [ ',' ] ] , '}' ;
mapEntry       = expression , ':' , expression ;
""";
}
